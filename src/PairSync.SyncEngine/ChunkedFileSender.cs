using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using PairSync.Protocol;
using PairSync.Transport;

namespace PairSync.SyncEngine;

public sealed record SenderOptions
{
    public string DeviceName { get; init; } = Environment.MachineName;

    /// <summary>Unacknowledged chunks allowed in flight; bounds memory to window × 4 MiB on both sides.</summary>
    public int Window { get; init; } = 16;

    /// <summary>Test switch: drop the connection after this many chunks were confirmed in this run.</summary>
    public int? AbortAfterChunks { get; init; }

    /// <summary>Test switch: corrupt the first transmission of this chunk to exercise Nack and retransmission.</summary>
    public int? CorruptChunkOnce { get; init; }
}

/// <summary>
/// Sends one file in 4 MiB chunks. Each chunk is hashed, split into data messages ≤ 256 KiB and
/// kept until the receiver acknowledges it; chunks the receiver already has are skipped (resume).
/// </summary>
public sealed class ChunkedFileSender(SenderOptions options)
{
    public TransferStats Stats { get; } = new();

    /// <summary>A stable id per file version, so a restarted sender resumes the same transfer.</summary>
    public static Guid TransferIdFor(FileInfo file)
    {
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }

    public async Task<TransferResult> SendAsync(ITransportSession session, FileInfo file, CancellationToken cancellationToken)
    {
        file.Refresh();
        var control = new ControlChannel(await session.GetChannelAsync("control", cancellationToken).ConfigureAwait(false));
        var data = await session.GetChannelAsync("data", cancellationToken).ConfigureAwait(false);

        await control.SendAsync(new Hello { DeviceName = options.DeviceName, MaxMessageSize = data.MaxMessageSize }, cancellationToken)
            .ConfigureAwait(false);
        var helloAck = await control.ExpectAsync<HelloAck>(cancellationToken).ConfigureAwait(false);
        var maxMessage = Math.Min(data.MaxMessageSize, helloAck.MaxMessageSize > 0 ? helloAck.MaxMessageSize : data.MaxMessageSize);

        var chunkCount = (int)Math.Max(1, (file.Length + ProtocolLimits.ChunkSize - 1) / ProtocolLimits.ChunkSize);
        var plan = new TransferPlan
        {
            TransferId = TransferIdFor(file),
            FileName = file.Name,
            FileSize = file.Length,
            ChunkSize = ProtocolLimits.ChunkSize,
            ChunkCount = chunkCount,
            LastWriteTimeUtc = file.LastWriteTimeUtc,
        };
        await control.SendAsync(plan, cancellationToken).ConfigureAwait(false);
        var planAck = await control.ExpectAsync<TransferPlanAck>(cancellationToken).ConfigureAwait(false);
        var confirmed = ChunkBitmap.FromBytes(planAck.ConfirmedChunks ?? [], chunkCount);

        Stats.FileName = file.Name;
        Stats.FileSize = file.Length;
        Stats.ChunkCount = chunkCount;
        Stats.ResumedChunks = confirmed.SetCount;
        Stats.TransferStarted();

        var events = Channel.CreateUnbounded<IControlMessage>(new UnboundedChannelOptions { SingleReader = true });
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = PumpControlAsync(control, events.Writer, readerCts.Token);
        try
        {
            await SendChunksAsync(file, plan, confirmed, control, data, maxMessage, events.Reader, cancellationToken).ConfigureAwait(false);
            return await AwaitResultAsync(events.Reader, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            readerCts.Cancel();
            await reader.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task SendChunksAsync(
        FileInfo file, TransferPlan plan, ChunkBitmap confirmed, ControlChannel control, IMessageChannel data, int maxMessage,
        ChannelReader<IControlMessage> events, CancellationToken cancellationToken)
    {
        var transferKey = DataFrameCodec.TransferKey(plan.TransferId);
        var payloadPerFragment = DataFrameCodec.PayloadPerFragment(maxMessage);
        var frame = new byte[DataFrameCodec.HeaderSize + DataFrameCodec.HashSize + payloadPerFragment];
        var inFlight = new Dictionary<int, (byte[] Buffer, int Length, byte[] Hash)>();
        var retransmit = new Queue<int>();
        var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var confirmedThisRun = 0;
        var paused = false;
        var corruptPending = options.CorruptChunkOnce;
        var next = 0;

        await using var stream = new FileStream(file.FullName, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            BufferSize = 0,
        });

        try
        {
            while (true)
            {
                while (events.TryRead(out var message))
                    Handle(message);

                if (!paused && retransmit.TryDequeue(out var again))
                {
                    if (inFlight.TryGetValue(again, out var pending))
                        await SendChunkAsync(again, pending.Buffer.AsMemory(0, pending.Length), pending.Hash, corrupt: false).ConfigureAwait(false);
                    continue;
                }

                if (!paused && next < plan.ChunkCount && inFlight.Count < options.Window)
                {
                    var index = next++;
                    var length = (int)Math.Min(plan.ChunkSize, plan.FileSize - (long)index * plan.ChunkSize);
                    var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    fileHash.AppendData(buffer, 0, length);

                    if (confirmed.IsSet(index))
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        continue;
                    }

                    var hash = SHA256.HashData(buffer.AsSpan(0, length));
                    inFlight[index] = (buffer, length, hash);
                    var corrupt = corruptPending == index;
                    if (corrupt)
                        corruptPending = null;
                    await SendChunkAsync(index, buffer.AsMemory(0, length), hash, corrupt).ConfigureAwait(false);
                    continue;
                }

                if (next >= plan.ChunkCount && inFlight.Count == 0)
                    break;

                Handle(await events.ReadAsync(cancellationToken).ConfigureAwait(false));
            }
        }
        catch (ChannelClosedException e)
        {
            throw new TransportException("Connection lost during the transfer.", e);
        }
        finally
        {
            foreach (var pending in inFlight.Values)
                ArrayPool<byte>.Shared.Return(pending.Buffer);
        }

        Stats.TransferEnded();

        // Plan §12: a file that changed during the transfer is not delivered.
        file.Refresh();
        if (file.Length != plan.FileSize || file.LastWriteTimeUtc != plan.LastWriteTimeUtc)
            throw new TransferCanceledException("the source file changed during the transfer");

        await control.SendAsync(new TransferFinish { TransferId = plan.TransferId, FileSha256 = fileHash.GetHashAndReset() }, cancellationToken)
            .ConfigureAwait(false);
        return;

        void Handle(IControlMessage message)
        {
            switch (message)
            {
                case ChunkAck ack when ack.TransferId == plan.TransferId:
                    if (inFlight.Remove(ack.ChunkIndex, out var done))
                    {
                        ArrayPool<byte>.Shared.Return(done.Buffer);
                        confirmed.Set(ack.ChunkIndex);
                        Stats.ChunkConfirmed();
                        confirmedThisRun++;
                        if (options.AbortAfterChunks is { } limit && confirmedThisRun >= limit)
                            throw new SimulatedDisconnectException(confirmedThisRun);
                    }
                    break;
                case ChunkNack nack when nack.TransferId == plan.TransferId:
                    if (inFlight.ContainsKey(nack.ChunkIndex))
                    {
                        retransmit.Enqueue(nack.ChunkIndex);
                        Stats.Retransmitted();
                    }
                    break;
                case Pause:
                    paused = true;
                    break;
                case Resume:
                    paused = false;
                    break;
                case Cancel cancel:
                    throw new TransferCanceledException(cancel.Reason ?? "canceled by the receiver");
            }
        }

        async Task SendChunkAsync(int index, ReadOnlyMemory<byte> chunk, byte[] hash, bool corrupt)
        {
            var fragments = DataFrameCodec.FragmentCount(chunk.Length, payloadPerFragment);
            for (var f = 0; f < fragments; f++)
            {
                var payload = chunk.Slice(f * payloadPerFragment, Math.Min(payloadPerFragment, chunk.Length - f * payloadPerFragment));
                var length = DataFrameCodec.Write(frame, transferKey, index, (ushort)f, (ushort)fragments, hash, payload.Span);
                if (corrupt && f == fragments - 1)
                    frame[length - 1] ^= 0xFF;
                await data.SendAsync(frame.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                Stats.AddBytes(payload.Length);
            }
        }
    }

    private async Task PumpControlAsync(ControlChannel control, ChannelWriter<IControlMessage> events, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in control.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (message is Ping ping)
                    await control.SendAsync(new Pong { Timestamp = ping.Timestamp }, cancellationToken).ConfigureAwait(false);
                else
                    events.TryWrite(message);
            }
            events.TryComplete();
        }
        catch (Exception e)
        {
            events.TryComplete(e is OperationCanceledException ? null : e);
        }
    }

    private static async Task<TransferResult> AwaitResultAsync(ChannelReader<IControlMessage> events, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                switch (await events.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    case TransferResult result:
                        return result;
                    case Cancel cancel:
                        throw new TransferCanceledException(cancel.Reason ?? "canceled by the receiver");
                }
            }
        }
        catch (ChannelClosedException e)
        {
            throw new TransportException("Connection lost before the receiver confirmed the file.", e);
        }
    }
}
