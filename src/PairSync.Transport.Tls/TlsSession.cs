using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PairSync.Transport.Tls;

/// <summary>
/// Named message channels multiplexed over one TLS 1.3 connection.
/// Frame: <c>[u8 channel index][u32 length, big endian][payload]</c>. TCP provides reliability,
/// ordering and backpressure; the kernel does the heavy lifting, so this reaches line rate in the LAN.
/// </summary>
internal sealed class TlsSession : ITransportSession
{
    private const int HeaderSize = 5;

    private readonly Socket _socket;
    private readonly SslStream _stream;
    private readonly TransportOptions _options;
    private readonly TlsMessageChannel[] _channels;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reader;
    private int _disposed;

    public TlsSession(Socket socket, SslStream stream, byte[] remotePublicKey, IReadOnlyList<string> channelLabels, TransportOptions options)
    {
        _socket = socket;
        RemotePublicKey = remotePublicKey;
        _stream = stream;
        _options = options;
        _channels = channelLabels.Select((label, index) => new TlsMessageChannel(this, (byte)index, label)).ToArray();
        Route = new RouteInfo(CandidateType.Host, socket.LocalEndPoint?.ToString() ?? "", CandidateType.Host, socket.RemoteEndPoint?.ToString() ?? "");
        State = TransportState.Connected;
        _reader = Task.Run(ReadLoopAsync);
    }

    public TransportState State { get; private set; }

    public RouteInfo? Route { get; }

    public byte[]? RemotePublicKey { get; }

    public event EventHandler<TransportState>? StateChanged;

    internal int MaxMessageSize => _options.MaxMessageSize;

    public Task ApplyAnswerAsync(SessionDescription answer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("TLS sessions are established directly; there is no answer to apply.");

    public Task<IMessageChannel> GetChannelAsync(string label, CancellationToken cancellationToken) =>
        _channels.FirstOrDefault(c => c.Label == label) is { } channel
            ? Task.FromResult<IMessageChannel>(channel)
            : throw new ArgumentException($"Unknown channel '{label}'.", nameof(label));

    internal async ValueTask SendAsync(byte channel, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (message.Length > _options.MaxMessageSize)
            throw new TransportException($"Message of {message.Length} bytes exceeds the channel limit of {_options.MaxMessageSize} bytes.");
        if (State != TransportState.Connected)
            throw new TransportException("Connection is closed.");

        // One write per frame keeps TLS records full and avoids a separate tiny record for the header.
        var buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + message.Length);
        try
        {
            buffer[0] = channel;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1), (uint)message.Length);
            message.Span.CopyTo(buffer.AsSpan(HeaderSize));
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(buffer.AsMemory(0, HeaderSize + message.Length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Close(TransportState.Disconnected, e.Message);
            throw new TransportException("Connection lost.", e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[HeaderSize];
        try
        {
            while (true)
            {
                await _stream.ReadExactlyAsync(header, _closing.Token).ConfigureAwait(false);
                var channel = header[0];
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
                if (channel >= _channels.Length || length > (uint)_options.MaxMessageSize)
                    throw new InvalidDataException($"Invalid frame (channel {channel}, {length} bytes).");
                var payload = new byte[length];
                await _stream.ReadExactlyAsync(payload, _closing.Token).ConfigureAwait(false);
                _channels[channel].Deliver(payload);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidDataException or EndOfStreamException)
        {
            Close(TransportState.Disconnected, e is EndOfStreamException ? null : e.Message);
        }
        catch (OperationCanceledException)
        {
            Close(TransportState.Closed, null);
        }
    }

    private void Close(TransportState state, string? error)
    {
        lock (_channels)
        {
            if (State is TransportState.Disconnected or TransportState.Closed)
                return;
            State = state;
        }
        foreach (var channel in _channels)
            channel.Complete(error);
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _closing.CancelAsync().ConfigureAwait(false);
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            // The other side reset the connection already; closing is all that is left to do.
        }
        _socket.Dispose();
        await _reader.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Close(TransportState.Closed, null);
        _closing.Dispose();
        _writeLock.Dispose();
    }
}

internal sealed class TlsMessageChannel(TlsSession session, byte index, string label) : IMessageChannel
{
    private readonly Channel<ReadOnlyMemory<byte>> _incoming =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public string Label { get; } = label;

    public bool IsOpen => session.State == TransportState.Connected;

    public int MaxMessageSize => session.MaxMessageSize;

    /// <summary>Always 0: <see cref="SendAsync"/> completes only once TCP accepted the bytes.</summary>
    public int BufferedAmount => 0;

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        session.SendAsync(index, message, cancellationToken);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return message;
    }

    internal void Deliver(byte[] payload) => _incoming.Writer.TryWrite(payload);

    internal void Complete(string? error) =>
        _incoming.Writer.TryComplete(error is null ? null : new TransportException(error));
}
