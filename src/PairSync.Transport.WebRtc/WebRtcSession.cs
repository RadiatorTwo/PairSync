using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using PairSync.Transport.WebRtc.Native;

namespace PairSync.Transport.WebRtc;

/// <summary>One libdatachannel PeerConnection (ICE + DTLS + SCTP) with its data channels.</summary>
internal sealed class WebRtcSession : ITransportSession
{
    private readonly TransportOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebRtcChannel>> _openChannels = new();
    private readonly ConcurrentDictionary<int, WebRtcChannel> _channels = new();
    private readonly TaskCompletionSource _gatheringComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<TransportState> _stateChanges =
        Channel.CreateUnbounded<TransportState>(new UnboundedChannelOptions { SingleReader = true });
    private GCHandle _self;
    private int _disposed;

    private unsafe WebRtcSession(TransportOptions options)
    {
        _options = options;
        _self = GCHandle.Alloc(this);
        _ = Task.Run(ProcessStateChangesAsync);
        Id = CreatePeerConnection(options);
        RtcNative.rtcSetUserPointer(Id, GCHandle.ToIntPtr(_self));
        RtcNative.Check(RtcNative.rtcSetStateChangeCallback(Id, &OnStateChange));
        RtcNative.Check(RtcNative.rtcSetGatheringStateChangeCallback(Id, &OnGatheringStateChange));
        RtcNative.Check(RtcNative.rtcSetDataChannelCallback(Id, &OnDataChannel));
    }

    public int Id { get; }

    public TransportState State { get; private set; } = TransportState.New;

    public RouteInfo? Route { get; private set; }

    /// <summary>Not bound to a device key yet; phase 2 checks the DTLS fingerprint against the paired key.</summary>
    public byte[]? RemotePublicKey => null;

    public event EventHandler<TransportState>? StateChanged;

    private static readonly Lock SctpLock = new();
    private static int _appliedSctpBufferSize = -1;

    public static WebRtcSession Create(TransportOptions options)
    {
        RtcLogger.EnsureInitialized();
        ApplySctpSettings(options);
        return new WebRtcSession(options);
    }

    /// <summary>SCTP settings are process-wide in libdatachannel and only affect PeerConnections created afterwards.</summary>
    private static unsafe void ApplySctpSettings(TransportOptions options)
    {
        lock (SctpLock)
        {
            if (_appliedSctpBufferSize == options.SctpBufferSize)
                return;
            var settings = new RtcSctpSettings { RecvBufferSize = options.SctpBufferSize, SendBufferSize = options.SctpBufferSize };
            RtcNative.Check(RtcNative.rtcSetSctpSettings(&settings));
            _appliedSctpBufferSize = options.SctpBufferSize;
        }
    }

    public unsafe void CreateChannel(string label)
    {
        var init = default(RtcDataChannelInit); // reliable and ordered
        var labelZ = RtcNative.Utf8Z(label);
        int dc;
        fixed (byte* l = labelZ)
            dc = RtcNative.Check(RtcNative.rtcCreateDataChannelEx(Id, l, &init));
        Register(new WebRtcChannel(this, dc, label, _options));
    }

    public unsafe void SetRemoteDescription(SessionDescription description)
    {
        var sdp = RtcNative.Utf8Z(description.Sdp);
        var type = RtcNative.Utf8Z(description.Type == SessionDescriptionType.Offer ? "offer" : "answer");
        fixed (byte* s = sdp)
        fixed (byte* t = type)
            RtcNative.Check(RtcNative.rtcSetRemoteDescription(Id, s, t));
    }

    /// <summary>Waits for ICE gathering to finish and returns the local description with all candidates.</summary>
    public async Task<SessionDescription> GetCompleteLocalDescriptionAsync(SessionDescriptionType type, CancellationToken cancellationToken)
    {
        await _gatheringComplete.Task.WaitAsync(_options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        var sdp = ReadLocalDescription() ?? throw new TransportException("No local session description available.");
        return new SessionDescription(type, sdp);
    }

    private unsafe string? ReadLocalDescription() =>
        RtcNative.GetString((buffer, size) => RtcNative.rtcGetLocalDescription(Id, buffer, size));

    public Task ApplyAnswerAsync(SessionDescription answer, CancellationToken cancellationToken)
    {
        if (answer.Type != SessionDescriptionType.Answer)
            throw new ArgumentException("Expected an answer.", nameof(answer));
        SetRemoteDescription(answer);
        return Task.CompletedTask;
    }

    public async Task<IMessageChannel> GetChannelAsync(string label, CancellationToken cancellationToken)
    {
        var pending = _openChannels.GetOrAdd(label, _ => NewChannelSignal());
        return await pending.Task.WaitAsync(_options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // rtcDelete* block until in-flight callbacks are done; keep them off the caller's context.
        await Task.Run(() =>
        {
            foreach (var channel in _channels.Values)
                channel.Delete();
            RtcNative.rtcDeletePeerConnection(Id);
        }).ConfigureAwait(false);
        if (_self.IsAllocated)
            _self.Free();
        _stateChanges.Writer.TryWrite(TransportState.Closed);
        _stateChanges.Writer.TryComplete();
    }

    private async Task ProcessStateChangesAsync()
    {
        await foreach (var state in _stateChanges.Reader.ReadAllAsync().ConfigureAwait(false))
            SetState(state);
    }

    internal void OnChannelOpen(WebRtcChannel channel) =>
        _openChannels.GetOrAdd(channel.Label, _ => NewChannelSignal()).TrySetResult(channel);

    private void Register(WebRtcChannel channel) => _channels[channel.Id] = channel;

    private void SetState(TransportState state)
    {
        if (State == state || State == TransportState.Closed)
            return;
        State = state;

        switch (state)
        {
            case TransportState.Connected:
                if (Volatile.Read(ref _disposed) == 0)
                    Route = ReadSelectedRoute();
                _connected.TrySetResult();
                break;
            case TransportState.Disconnected or TransportState.Failed or TransportState.Closed:
                var reason = $"Connection {state.ToString().ToLowerInvariant()}.";
                _connected.TrySetException(new TransportException(reason));
                foreach (var pending in _openChannels.Values)
                    pending.TrySetException(new TransportException(reason));
                foreach (var channel in _channels.Values)
                    channel.MarkClosed(state == TransportState.Closed ? null : reason);
                break;
        }

        StateChanged?.Invoke(this, state);
    }

    private unsafe RouteInfo? ReadSelectedRoute()
    {
        string? local = null, remote = null;
        var localBuf = new byte[512];
        var remoteBuf = new byte[512];
        fixed (byte* l = localBuf)
        fixed (byte* r = remoteBuf)
        {
            if (RtcNative.rtcGetSelectedCandidatePair(Id, l, localBuf.Length, r, remoteBuf.Length) >= 0)
            {
                local = RtcNative.FromUtf8Z(l);
                remote = RtcNative.FromUtf8Z(r);
            }
        }
        if (local is null || remote is null)
            return null;
        var (localType, localAddress) = CandidateParser.Parse(local);
        var (remoteType, remoteAddress) = CandidateParser.Parse(remote);
        return new RouteInfo(localType, localAddress, remoteType, remoteAddress);
    }

    private static unsafe int CreatePeerConnection(TransportOptions options)
    {
        var servers = options.IceServers.Select(RtcNative.Utf8Z).ToArray();
        var handles = servers.Select(s => GCHandle.Alloc(s, GCHandleType.Pinned)).ToArray();
        try
        {
            var pointers = stackalloc byte*[Math.Max(1, servers.Length)];
            for (var i = 0; i < handles.Length; i++)
                pointers[i] = (byte*)handles[i].AddrOfPinnedObject();

            var config = new RtcConfiguration
            {
                IceServers = servers.Length > 0 ? pointers : null,
                IceServersCount = servers.Length,
                PortRangeBegin = options.PortRangeBegin,
                PortRangeEnd = options.PortRangeEnd,
                MaxMessageSize = options.MaxMessageSize,
                Mtu = options.Mtu,
            };
            return RtcNative.Check(RtcNative.rtcCreatePeerConnection(&config));
        }
        finally
        {
            foreach (var handle in handles)
                handle.Free();
        }
    }

    private static TaskCompletionSource<WebRtcChannel> NewChannelSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WebRtcSession? From(nint ptr) =>
        ptr == 0 ? null : GCHandle.FromIntPtr(ptr).Target as WebRtcSession;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateChange(int pc, RtcState state, nint ptr)
    {
        var session = From(ptr);
        if (session is null)
            return;
        var mapped = state switch
        {
            RtcState.Connecting => TransportState.Connecting,
            RtcState.Connected => TransportState.Connected,
            RtcState.Disconnected => TransportState.Disconnected,
            RtcState.Failed => TransportState.Failed,
            RtcState.Closed => TransportState.Closed,
            _ => TransportState.New,
        };
        // Reading the selected pair calls back into libdatachannel; do it off the callback thread,
        // but keep the order of state changes.
        session._stateChanges.Writer.TryWrite(mapped);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGatheringStateChange(int pc, RtcGatheringState state, nint ptr)
    {
        if (state == RtcGatheringState.Complete)
            From(ptr)?._gatheringComplete.TrySetResult();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDataChannel(int pc, int dc, nint ptr)
    {
        var session = From(ptr);
        if (session is null)
            return;
        var label = RtcNative.GetString((buffer, size) => RtcNative.rtcGetDataChannelLabel(dc, buffer, size)) ?? string.Empty;
        session.Register(new WebRtcChannel(session, dc, label, session._options));
    }
}
