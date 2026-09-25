using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using PairSync.Transport.WebRtc.Native;

namespace PairSync.Transport.WebRtc;

/// <summary>
/// One libdatachannel PeerConnection (ICE + DTLS + SCTP) with its data channels. Stays usable for as long
/// as the connection holds: further channels can be opened at any time (<see cref="ISessionLink"/>).
/// </summary>
public sealed class WebRtcSession : ISessionLink
{
    private readonly TransportOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebRtcChannel>> _openChannels = new();
    private readonly ConcurrentDictionary<int, WebRtcChannel> _channels = new();
    private readonly TaskCompletionSource _gatheringComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<TransportState> _stateChanges =
        Channel.CreateUnbounded<TransportState>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<IMessageChannel> _accepted =
        Channel.CreateUnbounded<IMessageChannel>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private GCHandle _self;
    private int _disposed;
    private int _graceGeneration;
    private string? _remoteSdp;
    private byte[]? _remotePublicKey;
    private volatile TransportException? _terminalError;

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

    /// <summary>
    /// The device key the other side signed its description with, set by the caller after checking that
    /// signature (<see cref="BindRemotePublicKey"/>). libdatachannel only completes DTLS with the certificate
    /// whose fingerprint is in that signed description, so the key identifies the peer of this session.
    /// </summary>
    public byte[]? RemotePublicKey => Volatile.Read(ref _remotePublicKey);

    public event EventHandler<TransportState>? StateChanged;

    private static readonly Lock SctpLock = new();
    private static int _appliedSctpBufferSize = -1;

    internal static WebRtcSession Create(TransportOptions options)
    {
        RtcLogger.EnsureInitialized();
        ApplySctpSettings(options);
        return new WebRtcSession(options);
    }

    /// <summary>
    /// Binds the session to the device key that signed the remote description. Call it only after verifying
    /// that signature over the exact description passed to this session.
    /// </summary>
    public void BindRemotePublicKey(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        var bound = Interlocked.CompareExchange(ref _remotePublicKey, publicKey.ToArray(), null);
        if (bound is not null && !bound.AsSpan().SequenceEqual(publicKey))
            throw new InvalidOperationException("The session is already bound to a different device key.");
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

    internal unsafe void CreateChannel(string label)
    {
        var init = default(RtcDataChannelInit); // reliable and ordered
        var labelZ = RtcNative.Utf8Z(label);
        int dc;
        fixed (byte* l = labelZ)
            dc = RtcNative.Check(RtcNative.rtcCreateDataChannelEx(Id, l, &init));
        Register(new WebRtcChannel(this, dc, label, isRemote: false, _options));
    }

    internal unsafe void SetRemoteDescription(SessionDescription description)
    {
        var sdp = RtcNative.Utf8Z(description.Sdp);
        var type = RtcNative.Utf8Z(description.Type == SessionDescriptionType.Offer ? "offer" : "answer");
        fixed (byte* s = sdp)
        fixed (byte* t = type)
            RtcNative.Check(RtcNative.rtcSetRemoteDescription(Id, s, t));
        _remoteSdp = description.Sdp;
    }

    /// <summary>
    /// Waits for ICE gathering to finish and returns the local description with all candidates. If gathering
    /// takes longer than <see cref="TransportOptions.GatheringTimeout"/> (e.g. an unreachable STUN server),
    /// the description goes out with the candidates found so far.
    /// </summary>
    internal async Task<SessionDescription> GetCompleteLocalDescriptionAsync(SessionDescriptionType type, CancellationToken cancellationToken)
    {
        try
        {
            await _gatheringComplete.Task.WaitAsync(_options.GatheringTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Fall through with a partial description; without any candidate it is useless.
        }
        var sdp = ReadLocalDescription() ?? throw new TransportException("No local session description available.");
        if (!sdp.Contains("a=candidate:", StringComparison.Ordinal))
            throw new TransportException("ICE gathering found no usable network address.");
        return new SessionDescription(type, sdp);
    }

    private unsafe string? ReadLocalDescription() =>
        RtcNative.GetString((buffer, size) => RtcNative.rtcGetLocalDescription(Id, buffer, size));

    /// <summary>Only for the offering side: applies the answer and gives the connection <see cref="TransportOptions.ConnectTimeout"/> to come up.</summary>
    public Task ApplyAnswerAsync(SessionDescription answer, CancellationToken cancellationToken)
    {
        if (answer.Type != SessionDescriptionType.Answer)
            throw new ArgumentException("Expected an answer.", nameof(answer));
        SetRemoteDescription(answer);
        ArmConnectDeadline(_options.ConnectTimeout);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fails the session unless it is connected within <paramref name="timeout"/>. Until then nothing gives up on
    /// its own: the native ICE and DTLS timers are stretched for manual signaling.
    /// </summary>
    internal void ArmConnectDeadline(TimeSpan timeout)
    {
        _ = WatchAsync();

        async Task WatchAsync()
        {
            try
            {
                await Task.Delay(timeout, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (State is not TransportState.Connected)
                Fail($"No connection within {timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>Waits until the channel with this label is open on both sides, or the session fails.</summary>
    public async Task<IMessageChannel> GetChannelAsync(string label, CancellationToken cancellationToken)
    {
        var pending = _openChannels.GetOrAdd(label, _ => NewChannelSignal());
        // A signal added after the session ended would never complete otherwise.
        if (_terminalError is { } error)
            pending.TrySetException(error);
        return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IMessageChannel> OpenChannelAsync(string label, CancellationToken cancellationToken)
    {
        if (_terminalError is { } error)
            throw error;
        if (State is not TransportState.Connected)
            throw new TransportException("The session is not connected.");
        if (_openChannels.TryGetValue(label, out var existing) && existing.Task.IsCompletedSuccessfully)
            throw new InvalidOperationException($"Channel '{label}' is already open.");
        CreateChannel(label);
        return await GetChannelAsync(label, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IMessageChannel> AcceptChannelsAsync(CancellationToken cancellationToken) =>
        _accepted.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask CloseChannelAsync(IMessageChannel channel)
    {
        if (channel is not WebRtcChannel own)
            throw new ArgumentException("The channel does not belong to this session.", nameof(channel));
        // Both ends close a job's channels; whoever comes second finds it already released.
        Forget(own);
        await Task.Run(own.Delete).ConfigureAwait(false);
    }

    /// <remarks>The selected pair is read live: ICE can succeed while DTLS never completes (the other side never applied the answer).</remarks>
    public IceReport GetIceReport() => new(
        ReadLocalDescription() is { } local ? SdpInspector.CandidateTypes(local) : [],
        _remoteSdp is { } remote ? SdpInspector.CandidateTypes(remote) : [],
        Route ?? (Volatile.Read(ref _disposed) == 0 ? ReadSelectedRoute() : null));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
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
        _lifetime.Dispose();
    }

    private async Task ProcessStateChangesAsync()
    {
        await foreach (var state in _stateChanges.Reader.ReadAllAsync().ConfigureAwait(false))
            SetState(state);
    }

    internal void OnChannelOpen(WebRtcChannel channel)
    {
        _openChannels.GetOrAdd(channel.Label, _ => NewChannelSignal()).TrySetResult(channel);
        if (channel.IsRemote)
            _accepted.Writer.TryWrite(channel);
    }

    /// <summary>Called from a native callback when a channel closed; the release happens off that thread.</summary>
    internal void OnChannelClosed(WebRtcChannel channel)
    {
        if (Volatile.Read(ref _disposed) != 0 || _terminalError is not null)
            return;
        Forget(channel);
        _ = Task.Run(channel.Delete);
    }

    private void Forget(WebRtcChannel channel)
    {
        _channels.TryRemove(new KeyValuePair<int, WebRtcChannel>(channel.Id, channel));
        if (_openChannels.TryGetValue(channel.Label, out var signal)
            && signal.Task.IsCompletedSuccessfully && ReferenceEquals(signal.Task.Result, channel))
            _openChannels.TryRemove(new KeyValuePair<string, TaskCompletionSource<WebRtcChannel>>(channel.Label, signal));
    }

    private void Register(WebRtcChannel channel) => _channels[channel.Id] = channel;

    private void Fail(string reason)
    {
        _terminalError ??= new TransportException(reason);
        _stateChanges.Writer.TryWrite(TransportState.Failed);
    }

    private void SetState(TransportState state)
    {
        // Failed and Closed are final; a late native "connected" must not revive a session we gave up on.
        if (State == state || State == TransportState.Closed || (State == TransportState.Failed && state != TransportState.Closed))
            return;
        State = state;

        switch (state)
        {
            case TransportState.Connected:
                Interlocked.Increment(ref _graceGeneration);
                if (Volatile.Read(ref _disposed) == 0)
                    Route = ReadSelectedRoute();
                break;
            case TransportState.Disconnected:
                // ICE may recover (consent checks resume); channels stay usable until the grace period ends.
                StartGracePeriod(Interlocked.Increment(ref _graceGeneration));
                break;
            case TransportState.Failed or TransportState.Closed:
                var error = _terminalError ??= new TransportException($"Connection {state.ToString().ToLowerInvariant()}.");
                foreach (var pending in _openChannels.Values)
                    pending.TrySetException(error);
                foreach (var channel in _channels.Values)
                    channel.MarkClosed(state == TransportState.Closed ? null : error.Message);
                _accepted.Writer.TryComplete();
                break;
        }

        StateChanged?.Invoke(this, state);
    }

    private void StartGracePeriod(int generation)
    {
        _ = WatchAsync();

        async Task WatchAsync()
        {
            try
            {
                await Task.Delay(_options.DisconnectGracePeriod, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (Volatile.Read(ref _graceGeneration) == generation && State == TransportState.Disconnected)
                Fail("Connection lost.");
        }
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
        session.Register(new WebRtcChannel(session, dc, label, isRemote: true, session._options));
    }
}
