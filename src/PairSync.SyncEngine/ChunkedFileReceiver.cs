using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PairSync.Protocol;
using PairSync.Transport;

namespace PairSync.SyncEngine;

public sealed record ReceiverOptions
{
    public string DeviceName { get; init; } = Environment.MachineName;

    /// <summary>Largest file this receiver accepts (plan §11: size limits).</summary>
    public long MaxFileSize { get; init; } = 2L * 1024 * 1024 * 1024 * 1024;

    /// <summary>
    /// How often confirmed chunks are written to the journal during a transfer. A crash loses at most this much
    /// progress; the final state is always saved when the transfer stops.
    /// </summary>
    public TimeSpan JournalSaveInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

public sealed record ReceiveOutcome(bool Success, string? FinalPath, string Message);

/// <summary>
/// Receives one file: writes verified chunks into a temporary file in the target directory,
/// records them in the journal, checks the whole file at the end and moves it into place atomically.
/// An interrupted transfer keeps the temporary file and journal entry and resumes from them.
/// </summary>
public sealed class ChunkedFileReceiver(string targetDirectory, IChunkJournal journal, ReceiverOptions options)
{
    public static string TempPathFor(string targetPath) => targetPath + ".pairsync-tmp";

    public TransferStats Stats { get; } = new();

    public async Task<ReceiveOutcome> ReceiveAsync(ITransportSession session, CancellationToken cancellationToken)
    {
        var control = new ControlChannel(await session.GetChannelAsync("control", cancellationToken).ConfigureAwait(false));
        var data = await session.GetChannelAsync("data", cancellationToken).ConfigureAwait(false);

        await control.ExpectAsync<Hello>(cancellationToken).ConfigureAwait(false);
        await control.SendAsync(new HelloAck { DeviceName = options.DeviceName, MaxMessageSize = data.MaxMessageSize }, cancellationToken)
            .ConfigureAwait(false);

        var plan = await control.ExpectAsync<TransferPlan>(cancellationToken).ConfigureAwait(false);
        string targetPath;
        try
        {
            targetPath = Path.Combine(targetDirectory, ValidatePlan(plan));
            EnsureFreeSpace(targetPath, plan);
        }
        catch (ProtocolException e)
        {
            await control.SendAsync(new Cancel { TransferId = plan.TransferId, Reason = e.Message }, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var tempPath = TempPathFor(targetPath);
        var confirmed = await OpenJournalAsync(plan, tempPath, cancellationToken).ConfigureAwait(false);

        Stats.FileName = plan.FileName!;
        Stats.FileSize = plan.FileSize;
        Stats.ChunkCount = plan.ChunkCount;
        Stats.ResumedChunks = confirmed.SetCount;
        Stats.TransferStarted();

        TransferFinish finish;
        await using (var stream = OpenTempFile(tempPath, plan.FileSize))
        {
            await SaveJournalAsync(plan, tempPath, confirmed, cancellationToken).ConfigureAwait(false);
            await control.SendAsync(new TransferPlanAck { TransferId = plan.TransferId, ConfirmedChunks = confirmed.ToBytes() }, cancellationToken)
                .ConfigureAwait(false);

            using var loops = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var finishSignal = new TaskCompletionSource<TransferFinish>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dataTask = ReceiveChunksAsync(data, control, plan, stream.SafeFileHandle, tempPath, confirmed, loops.Token);
            var controlTask = WatchControlAsync(control, plan, finishSignal, loops.Token);

            var first = await Task.WhenAny(finishSignal.Task, dataTask, controlTask).ConfigureAwait(false);
            // The control loop returns right after signalling Finish, so check the signal, not which task won.
            if (!finishSignal.Task.IsCompletedSuccessfully)
            {
                loops.Cancel();
                // Wait for the data loop so the journal gets every chunk it wrote.
                await Task.WhenAll(dataTask, controlTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await SaveJournalAsync(plan, tempPath, confirmed, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await first.ConfigureAwait(false); // surfaces the failure
                    throw new TransportException("The connection closed before the transfer finished.");
                }
                catch (Exception e) when (e is not TransferCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    await TryCancelAsync(control, plan, e.Message).ConfigureAwait(false);
                    throw;
                }
            }

            finish = await finishSignal.Task.ConfigureAwait(false);
            Stats.TransferEnded();
            loops.Cancel();
            await Task.WhenAll(dataTask, controlTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        var outcome = await CompleteAsync(plan, finish, confirmed, targetPath, tempPath, cancellationToken).ConfigureAwait(false);
        await control.SendAsync(new TransferResult { TransferId = plan.TransferId, Success = outcome.Success, Message = outcome.Message }, cancellationToken)
            .ConfigureAwait(false);
        return outcome;
    }

    private async Task ReceiveChunksAsync(
        IMessageChannel data, ControlChannel control, TransferPlan plan, SafeFileHandle file,
        string tempPath, ChunkBitmap confirmed, CancellationToken cancellationToken)
    {
        var sinceSave = Stopwatch.StartNew();
        var transferKey = DataFrameCodec.TransferKey(plan.TransferId);
        var buffer = new byte[plan.ChunkSize];
        var current = -1;          // chunk being assembled, -1 if none
        var expectedFragment = 0;
        var fragmentCount = 0;
        var filled = 0;
        var chunkHash = new byte[DataFrameCodec.HashSize];
        var rejected = -1;         // chunk already nacked; skip its remaining fragments

        await foreach (var message in data.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var frame = DataFrameCodec.Read(message);
            if (frame.TransferKey != transferKey || frame.ChunkIndex >= plan.ChunkCount)
                throw new ProtocolException($"Data frame for an unknown transfer or chunk {frame.ChunkIndex}.");

            var index = frame.ChunkIndex;
            var chunkLength = ChunkLength(plan, index);

            if (frame.IsFirstFragment)
            {
                if (current >= 0 && current != index)
                    await NackAsync(current, "incomplete chunk").ConfigureAwait(false);
                current = index;
                rejected = -1;
                expectedFragment = 0;
                fragmentCount = frame.FragmentCount;
                filled = 0;
                frame.ChunkSha256.Span.CopyTo(chunkHash);
            }
            else if (index == rejected)
            {
                continue;
            }
            else if (index != current || frame.FragmentIndex != expectedFragment || frame.FragmentCount != fragmentCount)
            {
                await NackAsync(index, "fragment out of sequence").ConfigureAwait(false);
                if (current >= 0 && current != index)
                    await NackAsync(current, "incomplete chunk").ConfigureAwait(false);
                current = -1;
                rejected = index;
                continue;
            }

            if (filled + frame.Payload.Length > chunkLength)
            {
                await NackAsync(index, "chunk longer than planned").ConfigureAwait(false);
                current = -1;
                rejected = index;
                continue;
            }

            frame.Payload.Span.CopyTo(buffer.AsSpan(filled));
            filled += frame.Payload.Length;
            expectedFragment++;
            Stats.AddBytes(frame.Payload.Length);

            if (expectedFragment < fragmentCount)
                continue;

            current = -1;
            var chunk = buffer.AsMemory(0, filled);
            if (filled != chunkLength)
            {
                await NackAsync(index, "chunk shorter than planned").ConfigureAwait(false);
                continue;
            }
            if (!SHA256.HashData(chunk.Span).AsSpan().SequenceEqual(chunkHash))
            {
                await NackAsync(index, "hash mismatch").ConfigureAwait(false);
                continue;
            }

            if (!confirmed.IsSet(index))
            {
                try
                {
                    await RandomAccess.WriteAsync(file, chunk, (long)index * plan.ChunkSize, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException e) when (IsDiskFull(e))
                {
                    // Plan §12: keep the temporary file and journal so the transfer can resume later.
                    await control.SendAsync(new Cancel { TransferId = plan.TransferId, Reason = "target disk is full; progress kept, resume after freeing space" }, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
                confirmed.Set(index);
                Stats.ChunkConfirmed();
                if (sinceSave.Elapsed >= options.JournalSaveInterval)
                {
                    await SaveJournalAsync(plan, tempPath, confirmed, cancellationToken).ConfigureAwait(false);
                    sinceSave.Restart();
                }
            }
            await control.SendAsync(new ChunkAck { TransferId = plan.TransferId, ChunkIndex = index }, cancellationToken).ConfigureAwait(false);
        }

        throw new TransportException("The data channel closed during the transfer.");

        ValueTask NackAsync(int chunkIndex, string reason)
        {
            Stats.Retransmitted();
            return control.SendAsync(new ChunkNack { TransferId = plan.TransferId, ChunkIndex = chunkIndex, Reason = reason }, cancellationToken);
        }
    }

    /// <summary>Tells the sender why the transfer stopped, so it does not wait for a result that never comes.</summary>
    private static async Task TryCancelAsync(ControlChannel control, TransferPlan plan, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await control.SendAsync(new Cancel { TransferId = plan.TransferId, Reason = reason }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TransportException or OperationCanceledException)
        {
            // the connection is gone; the sender notices that on its own
        }
    }

    private static async Task WatchControlAsync(
        ControlChannel control, TransferPlan plan, TaskCompletionSource<TransferFinish> finish, CancellationToken cancellationToken)
    {
        await foreach (var message in control.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (message)
            {
                case TransferFinish done when done.TransferId == plan.TransferId:
                    finish.TrySetResult(done);
                    return;
                case Cancel cancel:
                    throw new TransferCanceledException(cancel.Reason ?? "canceled by the sender");
                case Ping ping:
                    await control.SendAsync(new Pong { Timestamp = ping.Timestamp }, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        throw new TransportException("The control channel closed during the transfer.");
    }

    private async Task<ReceiveOutcome> CompleteAsync(
        TransferPlan plan, TransferFinish finish, ChunkBitmap confirmed, string targetPath, string tempPath,
        CancellationToken cancellationToken)
    {
        if (!confirmed.IsComplete)
            return new ReceiveOutcome(false, null, $"Only {confirmed.SetCount} of {plan.ChunkCount} chunks were received.");

        byte[] actual;
        await using (var read = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
            actual = await SHA256.HashDataAsync(read, cancellationToken).ConfigureAwait(false);

        if (finish.FileSha256 is null || !actual.AsSpan().SequenceEqual(finish.FileSha256))
        {
            // Every chunk was verified, so a mismatch means the file is inconsistent: start over.
            File.Delete(tempPath);
            await journal.DeleteAsync(plan.TransferId, CancellationToken.None).ConfigureAwait(false);
            return new ReceiveOutcome(false, null, "Whole-file SHA-256 does not match; the temporary file was discarded.");
        }

        // Default policy "Keep both": never overwrite an existing file.
        var finalPath = UniquePath(targetPath);
        File.Move(tempPath, finalPath, overwrite: false);
        File.SetLastWriteTimeUtc(finalPath, plan.LastWriteTimeUtc);
        await journal.DeleteAsync(plan.TransferId, CancellationToken.None).ConfigureAwait(false);
        return new ReceiveOutcome(true, finalPath, $"Received and verified {Convert.ToHexStringLower(actual)}.");
    }

    private async Task<ChunkBitmap> OpenJournalAsync(TransferPlan plan, string tempPath, CancellationToken cancellationToken)
    {
        var existing = await journal.LoadAsync(plan.TransferId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Matches(plan, tempPath) && File.Exists(tempPath) && new FileInfo(tempPath).Length == plan.FileSize)
        {
            try
            {
                return ChunkBitmap.FromBytes(existing.Confirmed, plan.ChunkCount);
            }
            catch (FormatException)
            {
                // corrupt bitmap: start over below
            }
        }
        return new ChunkBitmap(plan.ChunkCount);
    }

    private Task SaveJournalAsync(TransferPlan plan, string tempPath, ChunkBitmap confirmed, CancellationToken cancellationToken) =>
        journal.SaveAsync(ChunkJournalEntry.For(plan, tempPath, confirmed), cancellationToken);

    private static FileStream OpenTempFile(string tempPath, long size)
    {
        var stream = new FileStream(tempPath, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.RandomAccess | FileOptions.Asynchronous,
            BufferSize = 0,
        });
        if (stream.Length != size)
            stream.SetLength(size);
        return stream;
    }

    /// <summary>Checks the plan against limits and returns a safe file name (plan §11: no traversal, no absolute paths).</summary>
    public string ValidatePlan(TransferPlan plan)
    {
        var name = plan.FileName;
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 ||
            name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name != name.Trim())
            throw new ProtocolException($"Rejected file name '{name}'.");
        if (plan.FileSize < 0 || plan.FileSize > options.MaxFileSize)
            throw new ProtocolException($"File size {plan.FileSize} is outside the accepted range.");
        if (plan.ChunkSize != ProtocolLimits.ChunkSize)
            throw new ProtocolException($"Unsupported chunk size {plan.ChunkSize}.");
        var expectedChunks = Math.Max(1, (plan.FileSize + plan.ChunkSize - 1) / plan.ChunkSize);
        if (plan.ChunkCount != expectedChunks)
            throw new ProtocolException($"Chunk count {plan.ChunkCount} does not match the file size.");
        return name;
    }

    private static void EnsureFreeSpace(string targetPath, TransferPlan plan)
    {
        var temp = new FileInfo(TempPathFor(targetPath));
        var needed = plan.FileSize - (temp.Exists ? temp.Length : 0);
        var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
        if (root is null || needed <= 0)
            return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < needed)
            throw new ProtocolException($"Target has {free / (1024 * 1024)} MiB free, the file needs {needed / (1024 * 1024)} MiB.");
    }

    private static int ChunkLength(TransferPlan plan, int index) =>
        (int)Math.Min(plan.ChunkSize, plan.FileSize - (long)index * plan.ChunkSize);

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static bool IsDiskFull(IOException e) =>
        (e.HResult & 0xFFFF) is 0x70 or 0x27 // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        || e.HResult == 28;                   // ENOSPC
}
