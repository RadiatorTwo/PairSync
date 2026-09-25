using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Domain;
using PairSync.Storage;
using PairSync.Storage.Identity;
using PairSync.Storage.Settings;
using PairSync.Stun;
using PairSync.Transport;
using PairSync.Transport.WebRtc;

namespace PairSync.Application.Internet;

public sealed record InternetOptions
{
    /// <summary>How long a connection code (and the offer behind it) stays valid.</summary>
    public TimeSpan OfferLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the answering side waits for the other side to apply its answer.</summary>
    public TimeSpan AnswerLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>After the answer is applied: time for ICE and DTLS to come up.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan GatheringTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a connected link may stay disconnected before it counts as lost.</summary>
    public TimeSpan DisconnectGracePeriod { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Classify the own NAT (a few STUN requests) when creating a code; off in tests without network.</summary>
    public bool DetectNat { get; init; } = true;
}

public enum InternetLinkPhase
{
    /// <summary>This device issued a connection code and waits for the answer code.</summary>
    WaitingForAnswer,

    /// <summary>This device answered a connection code and waits for the other side to apply the answer.</summary>
    WaitingForConnection,

    /// <summary>The answer is applied; ICE and DTLS are coming up.</summary>
    Connecting,

    Connected,

    /// <summary>The attempt failed or the connection was lost; <see cref="InternetLinkStatus.Error"/> says why.</summary>
    Failed,
}

/// <summary>What the UI shows for a device's internet connection.</summary>
public sealed record InternetLinkStatus(
    Guid DeviceId,
    string DeviceName,
    InternetLinkPhase Phase,
    DateTime? ExpiresAtUtc = null,
    RouteInfo? Route = null,
    TimeSpan? RoundTrip = null,
    string? Error = null,
    ConnectFailureReason? Failure = null,
    bool WasConnected = false);

/// <summary>The connection attempt failed; <see cref="Failure"/> is the NAT analysis, if ICE was the problem.</summary>
public sealed class InternetConnectException(string message, ConnectFailureReason? failure = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ConnectFailureReason? Failure { get; } = failure;
}

/// <summary>
/// Internet connections to paired devices by manual code exchange (plan §6 "Internet ohne Rendezvous", phase 2
/// block C). Device A creates a connection code, B answers it, A applies the answer; from then on an
/// <see cref="InternetLink"/> carries jobs in both directions until it drops. There is no automatic reconnect:
/// without a rendezvous service a new connection needs new codes.
/// </summary>
public sealed class InternetLinkService(
    CurrentIdentity identity, SettingsStore settings, IDbContextFactory<PairSyncDbContext> contexts, LanConnectionService lan,
    AnsweredOffers answered, InternetOptions options, TimeProvider time, ILogger<InternetLinkService> logger) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly CancellationTokenSource _stopping = new();
    private int _disposed;

    private sealed class Entry(Guid deviceId, string deviceName)
    {
        public Guid DeviceId { get; } = deviceId;

        public string DeviceName { get; set; } = deviceName;

        public InternetLinkPhase Phase { get; set; }

        public WebRtcSession? Session { get; set; }

        public byte[]? OfferNonce { get; set; }

        public DateTime? ExpiresAtUtc { get; set; }

        public NatHint LocalNat { get; set; }

        public NatHint RemoteNat { get; set; }

        public InternetLink? Link { get; set; }

        public string? Error { get; set; }

        public ConnectFailureReason? Failure { get; set; }

        public bool WasConnected { get; set; }
    }

    /// <summary>False if the native WebRTC library is missing; see <see cref="UnavailableReason"/>.</summary>
    public bool IsAvailable => WebRtcTransport.IsAvailable;

    public string? UnavailableReason => WebRtcTransport.UnavailableReason;

    /// <summary>A status changed (code issued, connected, lost, round trip measured).</summary>
    public event Action? Changed;

    /// <summary>A link to this device is up; waiting jobs can start.</summary>
    public event Action<PairedDevice>? LinkEstablished;

    public IReadOnlyList<InternetLinkStatus> Statuses
    {
        get
        {
            lock (_gate)
                return [.. _entries.Values.Select(ToStatus)];
        }
    }

    public InternetLinkStatus? StatusOf(Guid deviceId)
    {
        lock (_gate)
            return _entries.TryGetValue(deviceId, out var entry) ? ToStatus(entry) : null;
    }

    /// <summary>The open link to this device, or null.</summary>
    public InternetLink? LinkTo(Guid deviceId)
    {
        lock (_gate)
            return _entries.TryGetValue(deviceId, out var entry) && entry.Link is { IsOpen: true } link ? link : null;
    }

    /// <summary>Step 1 on device A: a connection code for <paramref name="deviceId"/>. Replaces any earlier attempt or link.</summary>
    /// <exception cref="InternetConnectException">Not possible (no WebRTC, not paired, blocked, no STUN, no network).</exception>
    public async Task<IssuedCode> CreateCodeAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        var device = await FindDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InternetConnectException("This device is not paired any more.");
        if (device.Trust == DeviceTrust.Blocked)
            throw new InternetConnectException($"{device.Name} is blocked. Unblock it in Devices to connect.");

        var servers = StunServers();
        var nat = DetectNatAsync(servers, cancellationToken);
        var (session, offer) = await CreateTransportAsync(servers, t => new WebRtcConnector(t).CreateOfferAsync(InternetLink.ChannelLabels, cancellationToken))
            .ConfigureAwait(false);
        var localNat = await nat.ConfigureAwait(false);
        if (!offer.Sdp.Contains("typ srflx", StringComparison.Ordinal))
            logger.LogWarning("Connection code for {Name} has no public address: STUN servers did not answer", device.Name);

        var nonce = RandomNumberGenerator.GetBytes(ConnectCodes.NonceSize);
        var expires = time.GetUtcNow().UtcDateTime + options.OfferLifetime;
        var text = ConnectCodes.CreateOffer(identity.Value.Identity, device.Id, offer, nonce, expires, localNat);

        var entry = await ReplaceAsync(device, e =>
        {
            e.Phase = InternetLinkPhase.WaitingForAnswer;
            e.Session = session;
            e.OfferNonce = nonce;
            e.ExpiresAtUtc = expires;
            e.LocalNat = localNat;
        }).ConfigureAwait(false);
        _ = ExpireAsync(entry, session, expires);
        logger.LogInformation("Created a connection code for {Name}", device.Name);
        return new IssuedCode(text, expires, nonce);
    }

    /// <summary>
    /// Step 2 on device B: checks a connection code from a paired device and returns the answer code to send back.
    /// The connection comes up once the other side applies it.
    /// </summary>
    /// <exception cref="InvalidConnectCodeException">The code is not valid, with a message for the user.</exception>
    /// <exception cref="InternetConnectException">Not possible here (no WebRTC, no network).</exception>
    public async Task<(PairedDevice Device, IssuedCode Answer)> AnswerCodeAsync(string text, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        List<PairedDevice> devices;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var own = identity.Value.Identity;
        var offer = ConnectCodes.ReadOffer(text, time.GetUtcNow().UtcDateTime, options.ClockSkew, own.Id,
            id => devices.SingleOrDefault(d => d.Id == id));
        if (!answered.TryUse(offer.Nonce, offer.ExpiresAtUtc, options.ClockSkew))
            throw new InvalidConnectCodeException("This connection code was already answered. Create a new one on the other device.");

        var servers = StunServers();
        var nat = DetectNatAsync(servers, cancellationToken);
        var (session, answer) = await CreateTransportAsync(servers,
            t => new WebRtcConnector(t).AcceptOfferAsync(offer.Offer, offer.Device.PublicKey, cancellationToken)).ConfigureAwait(false);
        var localNat = await nat.ConfigureAwait(false);
        var expires = time.GetUtcNow().UtcDateTime + options.AnswerLifetime;
        var code = ConnectCodes.CreateAnswer(own, settings.Current.EffectiveDeviceName, offer.Nonce, answer, expires, localNat);

        var entry = await ReplaceAsync(offer.Device, e =>
        {
            e.Phase = InternetLinkPhase.WaitingForConnection;
            e.Session = session;
            e.ExpiresAtUtc = expires;
            e.LocalNat = localNat;
            e.RemoteNat = offer.RemoteNat;
        }).ConfigureAwait(false);
        _ = CompleteAsync(entry, session, offer.Device);
        logger.LogInformation("Answered a connection code from {Name}", offer.Device.Name);
        return (offer.Device, new IssuedCode(code, expires, offer.Nonce));
    }

    /// <summary>Step 3 on device A: applies the answer code and waits until the connection is up.</summary>
    /// <exception cref="InvalidConnectCodeException">The answer is not valid or does not belong to an open code.</exception>
    /// <exception cref="InternetConnectException">No connection came up; <see cref="InternetConnectException.Failure"/> says why.</exception>
    public async Task<PairedDevice> ApplyAnswerAsync(string text, CancellationToken cancellationToken)
    {
        var answer = ConnectCodes.ReadAnswer(text, time.GetUtcNow().UtcDateTime, options.ClockSkew, identity.Value.Identity.Id);
        Entry entry;
        WebRtcSession session;
        lock (_gate)
        {
            entry = _entries.Values.SingleOrDefault(e => e.Phase == InternetLinkPhase.WaitingForAnswer
                                                         && e.OfferNonce is { } n && n.AsSpan().SequenceEqual(answer.OfferNonce))
                ?? throw new InvalidConnectCodeException(
                    "This answer code does not belong to an open connection code of this device. It may have expired or been answered already.");
            session = entry.Session!;
        }
        var device = await FindDeviceAsync(entry.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null || !answer.IsFrom(device))
            throw new InvalidConnectCodeException($"This answer code does not come from {entry.DeviceName}.");

        lock (_gate)
        {
            if (!ReferenceEquals(entry.Session, session) || entry.Phase != InternetLinkPhase.WaitingForAnswer)
                throw new InvalidConnectCodeException("This answer code was applied already.");
            entry.Phase = InternetLinkPhase.Connecting;
            entry.RemoteNat = answer.RemoteNat;
            entry.ExpiresAtUtc = null;
        }
        Changed?.Invoke();

        session.BindRemotePublicKey(answer.PublicKey);
        await session.ApplyAnswerAsync(answer.Answer, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(entry, session, device).ConfigureAwait(false);

        lock (_gate)
        {
            if (entry.Phase == InternetLinkPhase.Connected)
                return device;
            throw new InternetConnectException(entry.Error ?? "No connection.", entry.Failure);
        }
    }

    /// <summary>Ends the connection or attempt with this device and forgets its status.</summary>
    public async Task CloseAsync(Guid deviceId)
    {
        Entry? entry;
        lock (_gate)
            _entries.Remove(deviceId, out entry);
        if (entry is not null)
        {
            await DisposeEntryAsync(entry).ConfigureAwait(false);
            Changed?.Invoke();
        }
    }

    /// <summary>Forgets a failed attempt, so the device shows no internet status any more.</summary>
    public void Dismiss(Guid deviceId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(deviceId, out var entry) || entry.Phase != InternetLinkPhase.Failed)
                return;
            _entries.Remove(deviceId);
        }
        Changed?.Invoke();
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new InternetConnectException($"Internet connections are not available: {UnavailableReason}");
    }

    private IReadOnlyList<string> StunServers() =>
        [.. settings.Current.StunServers
            .Select(s => StunServerUri.TryParse(s, out var uri) ? uri.ToString() : null)
            .OfType<string>()];

    private async Task<(WebRtcSession Session, SessionDescription Description)> CreateTransportAsync(
        IReadOnlyList<string> servers, Func<TransportOptions, Task<(WebRtcSession, SessionDescription)>> create)
    {
        var transport = new TransportOptions
        {
            IceServers = servers,
            ConnectTimeout = options.ConnectTimeout,
            AnswerTimeout = options.AnswerLifetime,
            GatheringTimeout = options.GatheringTimeout,
            DisconnectGracePeriod = options.DisconnectGracePeriod,
        };
        try
        {
            return await create(transport).ConfigureAwait(false);
        }
        catch (TransportException e)
        {
            throw new InternetConnectException($"Could not prepare the connection: {e.Message}", inner: e);
        }
    }

    private Task<NatHint> DetectNatAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        if (!options.DetectNat || servers.Count == 0)
            return Task.FromResult(NatHint.Unknown);
        return Task.Run(async () =>
        {
            try
            {
                return await new NatDiagnostics().QuickHintAsync(servers, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "NAT detection failed");
                return NatHint.Unknown;
            }
        }, cancellationToken);
    }

    private async Task<PairedDevice?> FindDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Entry> ReplaceAsync(PairedDevice device, Action<Entry> setup)
    {
        var entry = new Entry(device.Id, device.Name);
        setup(entry);
        Entry? previous;
        lock (_gate)
        {
            _entries.Remove(device.Id, out previous);
            _entries[device.Id] = entry;
        }
        if (previous is not null)
            await DisposeEntryAsync(previous).ConfigureAwait(false);
        Changed?.Invoke();
        return entry;
    }

    /// <summary>A connection code nobody answered in time is dropped with its offer.</summary>
    private async Task ExpireAsync(Entry entry, WebRtcSession session, DateTime expiresAtUtc)
    {
        try
        {
            var remaining = expiresAtUtc - time.GetUtcNow().UtcDateTime;
            await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!TryFail(entry, session, InternetLinkPhase.WaitingForAnswer,
                "The connection code expired before an answer arrived. Create a new one.", null))
            return;
        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Waits for the connection of <paramref name="session"/> and turns it into a link, or records why it failed.</summary>
    private async Task CompleteAsync(Entry entry, WebRtcSession session, PairedDevice device)
    {
        try
        {
            // Opens once ICE and DTLS are up; the session deadline (AnswerTimeout or ConnectTimeout) ends the wait.
            await session.GetChannelAsync(InternetLink.LinkChannel, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TransportException or OperationCanceledException)
        {
            var report = session.GetIceReport();
            NatHint localNat, remoteNat;
            bool answering;
            lock (_gate)
                (localNat, remoteNat, answering) = (entry.LocalNat, entry.RemoteNat, entry.Phase == InternetLinkPhase.WaitingForConnection);
            var reason = ConnectFailureAnalysis.Analyze(localNat, remoteNat, ToFlags(report.LocalCandidates), ToFlags(report.RemoteCandidates));
            // ICE got through but DTLS never started: the other side did not apply the answer. Without an ICE path the
            // answering side cannot tell a network problem from an answer that never arrived.
            var message = !answering ? FailureMessage(reason, device.Name)
                : report.Selected is not null
                    ? $"No connection with {device.Name}: the answer code was not applied in time."
                    : reason == ConnectFailureReason.Unknown
                        ? $"No connection with {device.Name}: the answer code was not applied in time, or the networks did not let the connection through."
                        : FailureMessage(reason, device.Name);
            logger.LogInformation("Internet connection to {Name} failed: {Error} ({Reason}; local {Local}, remote {Remote})",
                device.Name, e.Message, reason, string.Join(",", report.LocalCandidates.Distinct()), string.Join(",", report.RemoteCandidates.Distinct()));
            if (TryFail(entry, session, null, message, reason))
                await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var link = new InternetLink(device, session, entry.RemoteNat, time, logger);
        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.DeviceId, out var current) || !ReferenceEquals(current, entry) || !ReferenceEquals(entry.Session, session))
                link = null;
            else
            {
                entry.Phase = InternetLinkPhase.Connected;
                entry.Link = link;
                entry.ExpiresAtUtc = null;
                entry.WasConnected = true;
            }
        }
        if (link is null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        link.RoundTripChanged += () => Changed?.Invoke();
        link.Start(lan.AcceptAsync, options.PingInterval);
        logger.LogInformation("Internet connection to {Name}: {Route}", device.Name, session.Route);
        _ = WatchAsync(entry, link);
        Changed?.Invoke();
        LinkEstablished?.Invoke(device);
    }

    private async Task WatchAsync(Entry entry, InternetLink link)
    {
        await link.Closed.ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0)
            return;
        bool lost;
        lock (_gate)
        {
            lost = _entries.TryGetValue(entry.DeviceId, out var current) && ReferenceEquals(current, entry) && ReferenceEquals(entry.Link, link);
            if (lost)
            {
                entry.Phase = InternetLinkPhase.Failed;
                entry.Link = null;
                entry.Session = null;
                entry.Error = "Connection lost. Exchange new codes to connect again.";
            }
        }
        if (lost)
        {
            await link.DisposeAsync().ConfigureAwait(false);
            Changed?.Invoke();
        }
    }

    /// <summary>Marks <paramref name="entry"/> failed if it still belongs to <paramref name="session"/> (and phase, if given).</summary>
    private bool TryFail(Entry entry, WebRtcSession session, InternetLinkPhase? expectedPhase, string error, ConnectFailureReason? reason)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(entry.Session, session) || (expectedPhase is { } phase && entry.Phase != phase) || entry.Phase == InternetLinkPhase.Connected)
                return false;
            entry.Phase = InternetLinkPhase.Failed;
            entry.Session = null;
            entry.ExpiresAtUtc = null;
            entry.Error = error;
            entry.Failure = reason;
        }
        Changed?.Invoke();
        return true;
    }

    internal static string FailureMessage(ConnectFailureReason reason, string deviceName) => reason switch
    {
        ConnectFailureReason.BothSymmetric =>
            $"Can't connect directly to {deviceName}. Both networks look like symmetric NAT / CGNAT, and no relay is configured.",
        ConnectFailureReason.OneSideSymmetric =>
            $"Can't connect directly to {deviceName}. One network uses symmetric NAT / CGNAT and the other one does not allow incoming connections.",
        ConnectFailureReason.LocalUdpBlocked => $"Can't connect to {deviceName}: this network blocks UDP.",
        ConnectFailureReason.RemoteUdpBlocked => $"Can't connect to {deviceName}: its network blocks UDP.",
        ConnectFailureReason.LocalNoPublicAddress =>
            $"Can't connect to {deviceName}: no public address found here. The STUN servers did not answer; check them in Settings.",
        ConnectFailureReason.RemoteNoPublicAddress =>
            $"Can't connect to {deviceName}: it found no public address. Its STUN servers did not answer.",
        _ => $"Can't connect directly to {deviceName}. The networks did not let the connection through.",
    };

    private static CandidateTypes ToFlags(IReadOnlyList<CandidateType> candidates) =>
        candidates.Aggregate(CandidateTypes.None, (flags, type) => flags | type switch
        {
            CandidateType.Host => CandidateTypes.Host,
            CandidateType.ServerReflexive => CandidateTypes.ServerReflexive,
            CandidateType.PeerReflexive => CandidateTypes.PeerReflexive,
            CandidateType.Relay => CandidateTypes.Relay,
            _ => CandidateTypes.None,
        });

    private InternetLinkStatus ToStatus(Entry e) => new(
        e.DeviceId, e.DeviceName, e.Phase, e.ExpiresAtUtc, e.Link?.Route, e.Link?.RoundTrip, e.Error, e.Failure, e.WasConnected);

    private static async Task DisposeEntryAsync(Entry entry)
    {
        if (entry.Link is { } link)
            await link.DisposeAsync().ConfigureAwait(false);
        else if (entry.Session is { } session)
            await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries.Values];
            _entries.Clear();
        }
        foreach (var entry in entries)
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        _stopping.Dispose();
    }
}
