using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using PairSync.Transport.WebRtc.Native;

namespace PairSync.Transport.WebRtc;

/// <summary>A reliable, ordered libdatachannel data channel with send-side backpressure.</summary>
internal sealed class WebRtcChannel : IMessageChannel
{
    private readonly WebRtcSession _session;
    private readonly TransportOptions _options;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });
    private GCHandle _self;
    private TaskCompletionSource _bufferLow = NewSignal();
    private volatile bool _open;
    private volatile bool _closed;
    private string? _error;

    public unsafe WebRtcChannel(WebRtcSession session, int dc, string label, TransportOptions options)
    {
        _session = session;
        _options = options;
        Id = dc;
        Label = label;
        _self = GCHandle.Alloc(this);

        RtcNative.rtcSetUserPointer(dc, GCHandle.ToIntPtr(_self));
        RtcNative.Check(RtcNative.rtcSetOpenCallback(dc, &OnOpen));
        RtcNative.Check(RtcNative.rtcSetClosedCallback(dc, &OnClosed));
        RtcNative.Check(RtcNative.rtcSetErrorCallback(dc, &OnError));
        RtcNative.Check(RtcNative.rtcSetBufferedAmountLowThreshold(dc, options.SendLowThreshold));
        RtcNative.Check(RtcNative.rtcSetBufferedAmountLowCallback(dc, &OnBufferedAmountLow));
        // Messages that arrived before this point are queued by libdatachannel and flushed now.
        RtcNative.Check(RtcNative.rtcSetMessageCallback(dc, &OnMessage));

        if (RtcNative.rtcIsOpen(dc))
            MarkOpen();
    }

    public int Id { get; }

    public string Label { get; }

    public bool IsOpen => _open && !_closed;

    public int MaxMessageSize
    {
        get
        {
            var remote = RtcNative.rtcMaxMessageSize(Id);
            return remote > 0 ? Math.Min(remote, _options.MaxMessageSize) : _options.MaxMessageSize;
        }
    }

    public int BufferedAmount => Math.Max(0, RtcNative.rtcGetBufferedAmount(Id));

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        while (true)
        {
            ThrowIfClosed();
            var signal = Volatile.Read(ref _bufferLow);
            if (BufferedAmount <= _options.SendHighWatermark)
                break;
            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (message.Length > MaxMessageSize)
            throw new TransportException($"Message of {message.Length} bytes exceeds the channel limit of {MaxMessageSize} bytes.");

        var result = SendNative(message.Span);
        if (result < 0)
        {
            ThrowIfClosed();
            throw new TransportException($"Sending on channel '{Label}' failed: {RtcNative.Describe(result)}.");
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return message;
    }

    private unsafe int SendNative(ReadOnlySpan<byte> message)
    {
        fixed (byte* p = message)
            return RtcNative.rtcSendMessage(Id, p, message.Length);
    }

    internal void Delete()
    {
        MarkClosed(null);
        RtcNative.rtcDelete(Id);
        if (_self.IsAllocated)
            _self.Free();
    }

    internal void MarkClosed(string? error)
    {
        if (_closed)
            return;
        _error ??= error;
        _closed = true;
        _incoming.Writer.TryComplete(error is null ? null : new TransportException(error));
        Interlocked.Exchange(ref _bufferLow, NewSignal()).TrySetResult();
    }

    private void MarkOpen()
    {
        _open = true;
        _session.OnChannelOpen(this);
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new TransportException(_error is null ? $"Channel '{Label}' is closed." : $"Channel '{Label}' failed: {_error}");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WebRtcChannel? From(nint ptr) =>
        ptr == 0 ? null : GCHandle.FromIntPtr(ptr).Target as WebRtcChannel;

    // Native callbacks run on libdatachannel threads: hand off, never block, never call rtcDelete here.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOpen(int id, nint ptr) => From(ptr)?.MarkOpen();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClosed(int id, nint ptr) => From(ptr)?.MarkClosed(null);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnError(int id, byte* error, nint ptr) =>
        From(ptr)?.MarkClosed(RtcNative.FromUtf8Z(error) ?? "unknown error");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBufferedAmountLow(int id, nint ptr)
    {
        if (From(ptr) is { } channel)
            Interlocked.Exchange(ref channel._bufferLow, NewSignal()).TrySetResult();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnMessage(int id, byte* data, int size, nint ptr)
    {
        // size < 0 marks a text message; PairSync only uses binary messages and drops text.
        if (size < 0 || From(ptr) is not { } channel)
            return;
        var copy = new ReadOnlySpan<byte>(data, size).ToArray();
        channel._incoming.Writer.TryWrite(copy);
    }
}
