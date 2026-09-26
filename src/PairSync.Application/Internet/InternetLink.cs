using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Stun;
using PairSync.SyncEngine;
using PairSync.Transport;
using PairSync.Transport.WebRtc;

namespace PairSync.Application.Internet;

/// <summary>
/// An established internet connection to a paired device (phase 2 block C). It stays open until either side quits or
/// the network drops it, and carries any number of jobs in both directions: each job opens its own pair of channels
/// (<see cref="OpenSessionAsync"/>) and closes them when done. The <c>link</c> channel carries Ping/Pong for the
/// round-trip time and as a sign of life, and a Goodbye before a side closes the link on purpose.
/// </summary>
public sealed class InternetLink : IAsyncDisposable
{
    public const string LinkChannel = "link";

    public static readonly string[] ChannelLabels = [LinkChannel];

    private const string ControlPrefix = "c:";
    private const string DataPrefix = "d:";

    /// <summary>How long closing waits for the Goodbye to go out.</summary>
    private static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private Task _run = Task.CompletedTask;
    private ControlChannel? _control;
    private long _roundTripTicks = -1;
    private int _closedByPeer;
    private int _disposed;

    internal InternetLink(PairedDevice device, WebRtcSession session, NatHint remoteNat, TimeProvider time, ILogger logger)
    {
        Device = device;
        Session = session;
        RemoteNat = remoteNat;
        _time = time;
        _logger = logger;
        ConnectedAtUtc = time.GetUtcNow().UtcDateTime;
    }

    /// <remarks>During an internet pairing a provisional entry from the codes; the stored device once pairing completed.</remarks>
    public PairedDevice Device { get; internal set; }

    internal WebRtcSession Session { get; }

    public NatHint RemoteNat { get; }

    public DateTime ConnectedAtUtc { get; }

    public RouteInfo? Route => Session.Route;

    /// <summary>Last measured round trip over the link channel; null before the first Pong.</summary>
    public TimeSpan? RoundTrip => Interlocked.Read(ref _roundTripTicks) is var ticks and >= 0 ? TimeSpan.FromTicks(ticks) : null;

    public bool IsOpen => !_closed.Task.IsCompleted;

    /// <summary>Completes when the connection is gone (lost, closed by either side, or disposed).</summary>
    public Task Closed => _closed.Task;

    /// <summary>The other side said Goodbye: it quit or disconnected on purpose, the connection was not lost.</summary>
    public bool ClosedByPeer => Volatile.Read(ref _closedByPeer) != 0;

    /// <summary>Raised after a new round-trip measurement.</summary>
    public event Action? RoundTripChanged;

    /// <param name="acceptSession">Takes over a job session the other side opened; runs the answering handshake.</param>
    internal void Start(Func<ITransportSession, Task> acceptSession, TimeSpan pingInterval)
    {
        Session.StateChanged += OnStateChanged;
        if (Session.State is TransportState.Failed or TransportState.Closed)
            OnStateChanged(Session, Session.State);
        _run = Task.WhenAll(
            Task.Run(() => PingAsync(pingInterval, _stopping.Token)),
            Task.Run(() => AcceptAsync(acceptSession, _stopping.Token)));
    }

    /// <summary>Opens a job session (its own control and data channel) to the other device.</summary>
    /// <exception cref="TransportException">The link is gone.</exception>
    public async Task<ITransportSession> OpenSessionAsync(CancellationToken cancellationToken)
    {
        var session = new LinkSession(this, Guid.NewGuid().ToString("N"));
        try
        {
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnStateChanged(object? sender, TransportState state)
    {
        if (state is not (TransportState.Failed or TransportState.Closed))
            return;
        if (_closed.TrySetResult())
            _logger.LogInformation("Internet connection to {Name} ended ({State})", Device.Name, state);
        _stopping.Cancel();
    }

    private async Task PingAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            var control = new ControlChannel(await Session.GetChannelAsync(LinkChannel, cancellationToken).ConfigureAwait(false));
            Volatile.Write(ref _control, control);
            var replies = Task.Run(async () =>
            {
                await foreach (var message in control.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    switch (message)
                    {
                        case Ping ping:
                            await control.SendAsync(new Pong { Timestamp = ping.Timestamp }, cancellationToken).ConfigureAwait(false);
                            break;
                        case Pong pong:
                            Interlocked.Exchange(ref _roundTripTicks, Stopwatch.GetElapsedTime(pong.Timestamp).Ticks);
                            RoundTripChanged?.Invoke();
                            break;
                        case Goodbye:
                            Volatile.Write(ref _closedByPeer, 1);
                            if (_closed.TrySetResult())
                                _logger.LogInformation("{Name} closed the internet connection", Device.Name);
                            _stopping.Cancel();
                            break;
                    }
                }
            }, cancellationToken);

            while (!cancellationToken.IsCancellationRequested && !replies.IsCompleted)
            {
                await control.SendAsync(new Ping { Timestamp = Stopwatch.GetTimestamp() }, cancellationToken).ConfigureAwait(false);
                await Task.WhenAny(replies, Task.Delay(interval, _time, cancellationToken)).ConfigureAwait(false);
            }
            await replies.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TransportException or ProtocolException or ObjectDisposedException)
        {
        }
    }

    private async Task AcceptAsync(Func<ITransportSession, Task> acceptSession, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var channel in Session.AcceptChannelsAsync(cancellationToken).ConfigureAwait(false))
            {
                // A job opens its control channel first; the data channel is picked up by label in the handshake.
                if (!channel.Label.StartsWith(ControlPrefix, StringComparison.Ordinal))
                    continue;
                var session = new LinkSession(this, channel.Label[ControlPrefix.Length..]);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await acceptSession(session).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        _logger.LogDebug(e, "Incoming job session from {Name} failed", Device.Name);
                        await session.DisposeAsync().ConfigureAwait(false);
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await SayGoodbyeAsync().ConfigureAwait(false);
        Session.StateChanged -= OnStateChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        _closed.TrySetResult();
        await Session.DisposeAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stopping.Dispose();
    }

    /// <summary>Tells the other side that this side closes the link on purpose; skipped if the link is gone already.</summary>
    private async Task SayGoodbyeAsync()
    {
        if (!IsOpen || Volatile.Read(ref _control) is not { } control)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(GoodbyeTimeout, _time);
            await control.SendAsync(new Goodbye(), timeout.Token).ConfigureAwait(false);
            // The other side closes the link when it reads the Goodbye; closing first could drop the message.
            await Task.WhenAny(_closed.Task, Task.Delay(GoodbyeTimeout, _time)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TransportException or ProtocolException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// One job's view of the link: <c>control</c> and <c>data</c> map to this job's own channels, identity and route
    /// come from the link. Disposing closes only these channels, never the link.
    /// </summary>
    private sealed class LinkSession : ITransportSession
    {
        private readonly InternetLink _link;
        private readonly string _id;
        private readonly Lock _gate = new();
        private readonly List<IMessageChannel> _channels = [];
        private int _disposed;

        public LinkSession(InternetLink link, string id)
        {
            _link = link;
            _id = id;
            link.Session.StateChanged += Forward;
        }

        public TransportState State => _link.Session.State;

        public RouteInfo? Route => _link.Route;

        public byte[]? RemotePublicKey => _link.Session.RemotePublicKey;

        public event EventHandler<TransportState>? StateChanged;

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            Track(await _link.Session.OpenChannelAsync(ControlPrefix + _id, cancellationToken).ConfigureAwait(false));
            Track(await _link.Session.OpenChannelAsync(DataPrefix + _id, cancellationToken).ConfigureAwait(false));
        }

        public Task ApplyAnswerAsync(SessionDescription answer, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A job session runs on an established link.");

        public async Task<IMessageChannel> GetChannelAsync(string label, CancellationToken cancellationToken)
        {
            var mapped = label switch
            {
                "control" => ControlPrefix + _id,
                "data" => DataPrefix + _id,
                _ => throw new ArgumentException($"A job session has no channel '{label}'.", nameof(label)),
            };
            return Track(await _link.Session.GetChannelAsync(mapped, cancellationToken).ConfigureAwait(false));
        }

        private IMessageChannel Track(IMessageChannel channel)
        {
            lock (_gate)
            {
                if (!_channels.Contains(channel))
                    _channels.Add(channel);
            }
            return channel;
        }

        private void Forward(object? sender, TransportState state) => StateChanged?.Invoke(this, state);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _link.Session.StateChanged -= Forward;
            IMessageChannel[] channels;
            lock (_gate)
                channels = [.. _channels];
            foreach (var channel in channels)
            {
                try
                {
                    await _link.Session.CloseChannelAsync(channel).ConfigureAwait(false);
                }
                catch (Exception e) when (e is TransportException or ObjectDisposedException)
                {
                }
            }
        }
    }
}
