using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Application.Pairing;
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

/// <summary>What an answer code (<c>PSR1</c>) answers; see <see cref="InternetLinkService.TargetOfAnswer"/>.</summary>
public enum AnswerTarget
{
    ConnectionCode,
    Invitation,
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
    private readonly Dictionary<string, PairingOffer> _pairingOffers = [];
    private readonly Dictionary<Guid, PairingLink> _pairingLinks = [];
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

    /// <summary>The WebRTC offer inside an internet invitation (<c>PSI2</c>), waiting for the answer of a new device.</summary>
    private sealed record PairingOffer(WebRtcSession Session, NatHint LocalNat);

    /// <summary>
    /// A connection to a device that is being paired (block D). Until both users confirmed the security code it only
    /// carries the pairing session; afterwards it becomes the device's internet link (<see cref="AdoptPairingLinkAsync"/>).
    /// </summary>
    private sealed record PairingLink(InternetLink Link, NatHint LocalNat)
    {
        public bool Claimed { get; set; }
    }

    /// <summary>False if the native WebRTC library is missing; see <see cref="UnavailableReason"/>.</summary>
    public bool IsAvailable => WebRtcTransport.IsAvailable;

    public string? UnavailableReason => WebRtcTransport.UnavailableReason;

    /// <summary>
    /// New invitations carry a WebRTC offer (<c>PSI2</c>): WebRTC is available and STUN servers are set, without
    /// which the offer would hold no public address.
    /// </summary>
    public bool CanInviteOverInternet => IsAvailable && StunServers().Count > 0;

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

    /// <summary>
    /// Block D, device A: the WebRTC offer for an internet invitation with this nonce. It waits for the new device's
    /// answer until the invitation expires or is revoked (<see cref="RevokePairingOffer"/>).
    /// </summary>
    /// <exception cref="InternetConnectException">Not possible (no WebRTC, no network).</exception>
    internal async Task<(SessionDescription Offer, NatHint Nat)> CreatePairingOfferAsync(
        byte[] nonce, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        var servers = StunServers();
        var nat = DetectNatAsync(servers, cancellationToken);
        var (session, offer) = await CreateTransportAsync(servers, t => new WebRtcConnector(t).CreateOfferAsync(InternetLink.ChannelLabels, cancellationToken))
            .ConfigureAwait(false);
        var localNat = await nat.ConfigureAwait(false);
        var key = Convert.ToHexString(nonce);
        lock (_gate)
            _pairingOffers[key] = new PairingOffer(session, localNat);
        _ = ExpirePairingOfferAsync(key, session, expiresAtUtc);
        return (offer, localNat);
    }

    /// <summary>The invitation was closed or replaced; its offer can no longer be answered.</summary>
    internal void RevokePairingOffer(byte[] nonce)
    {
        PairingOffer? offer;
        lock (_gate)
            _pairingOffers.Remove(Convert.ToHexString(nonce), out offer);
        if (offer is not null)
            _ = offer.Session.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Block D, device B: answers the offer of an internet invitation. <c>Connected</c> completes with the link once
    /// A applied the answer and ICE got through; until <see cref="AdoptPairingLinkAsync"/> the link carries only the pairing.
    /// </summary>
    /// <exception cref="InvalidConnectCodeException">This invitation was answered already.</exception>
    /// <exception cref="InternetConnectException">Not possible here (no WebRTC, no network).</exception>
    internal async Task<(IssuedCode Answer, Task<InternetLink> Connected)> AnswerInvitationAsync(
        ReceivedInvitation invitation, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        if (invitation.Offer is not { } offer)
            throw new ArgumentException("The invitation carries no internet offer.", nameof(invitation));
        if (!answered.TryUse(invitation.Nonce, invitation.ExpiresAtUtc, options.ClockSkew))
            throw new InvalidConnectCodeException("This invitation was already answered. Create a new one on the other device.");

        var servers = StunServers();
        var nat = DetectNatAsync(servers, cancellationToken);
        var (session, answer) = await CreateTransportAsync(servers,
            t => new WebRtcConnector(t).AcceptOfferAsync(offer, invitation.PublicKey, cancellationToken)).ConfigureAwait(false);
        var localNat = await nat.ConfigureAwait(false);
        var expires = time.GetUtcNow().UtcDateTime + options.AnswerLifetime;
        var code = ConnectCodes.CreateAnswer(identity.Value.Identity, settings.Current.EffectiveDeviceName, invitation.Nonce, answer, expires, localNat);
        var device = new PairedDevice { Id = invitation.DeviceId, Name = invitation.DeviceName, PublicKey = invitation.PublicKey };
        logger.LogInformation("Answered an internet invitation from {Name}", device.Name);
        return (new IssuedCode(code, expires, invitation.Nonce),
            ConnectPairingAsync(session, device, localNat, invitation.RemoteNat, answering: true, cancellationToken));
    }

    /// <summary>Block D, device A: applies the new device's answer to the offer of an open invitation and waits for the connection.</summary>
    /// <exception cref="InvalidConnectCodeException">No open invitation offer with this nonce.</exception>
    /// <exception cref="InternetConnectException">No connection came up; <see cref="InternetConnectException.Failure"/> says why.</exception>
    internal async Task<InternetLink> ConnectInvitationAsync(ReceivedAnswer answer, CancellationToken cancellationToken)
    {
        PairingOffer? offer;
        lock (_gate)
            _pairingOffers.Remove(Convert.ToHexString(answer.OfferNonce), out offer);
        if (offer is null)
            throw new InvalidConnectCodeException(
                "This answer code does not belong to an open invitation of this device. It may have expired or been used already.");

        try
        {
            offer.Session.BindRemotePublicKey(answer.PublicKey);
            await offer.Session.ApplyAnswerAsync(answer.Answer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await offer.Session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var device = new PairedDevice { Id = answer.DeviceId, Name = answer.DeviceName, PublicKey = answer.PublicKey };
        return await ConnectPairingAsync(offer.Session, device, offer.LocalNat, answer.RemoteNat, answering: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What a pasted answer code belongs to: an open connection code (<see cref="ApplyAnswerAsync"/>) or an open
    /// internet invitation (<see cref="PairingService.ApplyInvitationAnswerAsync"/>).
    /// </summary>
    /// <exception cref="InvalidConnectCodeException">Not a valid answer code, or it belongs to neither.</exception>
    public AnswerTarget TargetOfAnswer(string text)
    {
        var answer = ConnectCodes.ReadAnswer(text, time.GetUtcNow().UtcDateTime, options.ClockSkew, identity.Value.Identity.Id);
        lock (_gate)
        {
            if (_entries.Values.Any(e => e.Phase == InternetLinkPhase.WaitingForAnswer && e.OfferNonce is { } n && n.AsSpan().SequenceEqual(answer.OfferNonce)))
                return AnswerTarget.ConnectionCode;
            if (_pairingOffers.ContainsKey(Convert.ToHexString(answer.OfferNonce)))
                return AnswerTarget.Invitation;
        }
        throw new InvalidConnectCodeException(
            "This answer code does not belong to an open connection code or invitation of this device. It may have expired or been used already.");
    }

    /// <summary>An internet connection to this device is open for a pairing that has not completed yet.</summary>
    public bool HasPairingConnection(Guid deviceId)
    {
        lock (_gate)
            return _pairingLinks.TryGetValue(deviceId, out var pairing) && pairing.Link.IsOpen;
    }

    /// <summary>A pairing session runs over the pairing link to this device; from now on that session decides its end.</summary>
    internal void ClaimPairingLink(Guid deviceId)
    {
        lock (_gate)
        {
            if (_pairingLinks.TryGetValue(deviceId, out var pairing))
                pairing.Claimed = true;
        }
    }

    /// <summary>
    /// Pairing completed: the pairing link to <paramref name="device"/> stays open as its internet link. No effect
    /// without a pairing link, or if that link is bound to another key than the stored one.
    /// </summary>
    internal async Task AdoptPairingLinkAsync(PairedDevice device)
    {
        PairingLink? pairing;
        lock (_gate)
        {
            if (!_pairingLinks.TryGetValue(device.Id, out pairing) || !pairing.Link.Device.PublicKey.AsSpan().SequenceEqual(device.PublicKey))
                return;
            _pairingLinks.Remove(device.Id);
        }
        var link = pairing.Link;
        if (!link.IsOpen)
        {
            await link.DisposeAsync().ConfigureAwait(false);
            return;
        }
        link.Device = device;
        var entry = await ReplaceAsync(device, e =>
        {
            e.Phase = InternetLinkPhase.Connected;
            e.Session = link.Session;
            e.Link = link;
            e.LocalNat = pairing.LocalNat;
            e.RemoteNat = link.RemoteNat;
            e.WasConnected = true;
        }).ConfigureAwait(false);
        link.RoundTripChanged += () => Changed?.Invoke();
        logger.LogInformation("Internet connection to the newly paired {Name}: {Route}", device.Name, link.Route);
        _ = WatchAsync(entry, link);
        LinkEstablished?.Invoke(device);
    }

    /// <summary>Pairing failed or was canceled: the connection it ran over is closed.</summary>
    internal async Task DiscardPairingLinkAsync(Guid deviceId)
    {
        PairingLink? pairing;
        lock (_gate)
            _pairingLinks.Remove(deviceId, out pairing);
        if (pairing is not null)
        {
            logger.LogInformation("Closed the pairing connection to {Name}", pairing.Link.Device.Name);
            await pairing.Link.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Waits until <paramref name="session"/> is up and keeps it as the pairing link to <paramref name="device"/>.</summary>
    private async Task<InternetLink> ConnectPairingAsync(
        WebRtcSession session, PairedDevice device, NatHint localNat, NatHint remoteNat, bool answering, CancellationToken cancellationToken)
    {
        try
        {
            // The session deadline (AnswerTimeout or ConnectTimeout) ends the wait.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            await session.GetChannelAsync(InternetLink.LinkChannel, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is TransportException or OperationCanceledException)
        {
            var (message, reason) = DescribeFailure(session, localNat, remoteNat, answering, device.Name, e);
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InternetConnectException(message, reason, e);
        }

        var link = new InternetLink(device, session, remoteNat, time, logger);
        var pairing = new PairingLink(link, localNat);
        PairingLink? previous;
        lock (_gate)
        {
            _pairingLinks.Remove(device.Id, out previous);
            _pairingLinks[device.Id] = pairing;
        }
        if (previous is not null)
            await previous.Link.DisposeAsync().ConfigureAwait(false);
        link.Start(lan.AcceptAsync, options.PingInterval);
        logger.LogInformation("Internet connection for pairing with {Name}: {Route}", device.Name, session.Route);
        _ = ExpirePairingLinkAsync(pairing);
        return link;
    }

    /// <summary>An invitation offer nobody answered in time is dropped.</summary>
    private async Task ExpirePairingOfferAsync(string key, WebRtcSession session, DateTime expiresAtUtc)
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
        lock (_gate)
        {
            if (!_pairingOffers.TryGetValue(key, out var offer) || !ReferenceEquals(offer.Session, session))
                return;
            _pairingOffers.Remove(key);
        }
        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A pairing link that no pairing session took over in time is closed.</summary>
    private async Task ExpirePairingLinkAsync(PairingLink pairing)
    {
        await Task.WhenAny(pairing.Link.Closed, Task.Delay(options.ConnectTimeout, time, _stopping.Token)).ConfigureAwait(false);
        var id = pairing.Link.Device.Id;
        lock (_gate)
        {
            if (!_pairingLinks.TryGetValue(id, out var current) || !ReferenceEquals(current, pairing) || (pairing.Claimed && pairing.Link.IsOpen))
                return;
            _pairingLinks.Remove(id);
        }
        await pairing.Link.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>The user renamed the device: its status and link show the new name.</summary>
    internal void Rename(Guid deviceId, string name)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(deviceId, out var entry))
                return;
            entry.DeviceName = name;
            if (entry.Link is { } link)
                link.Device.Name = name;
        }
        Changed?.Invoke();
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
            NatHint localNat, remoteNat;
            bool answering;
            lock (_gate)
                (localNat, remoteNat, answering) = (entry.LocalNat, entry.RemoteNat, entry.Phase == InternetLinkPhase.WaitingForConnection);
            var (message, reason) = DescribeFailure(session, localNat, remoteNat, answering, device.Name, e);
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

    /// <summary>Why ICE or DTLS did not come up, for the user; logs the details.</summary>
    private (string Message, ConnectFailureReason Reason) DescribeFailure(
        WebRtcSession session, NatHint localNat, NatHint remoteNat, bool answering, string deviceName, Exception e)
    {
        var report = session.GetIceReport();
        var reason = ConnectFailureAnalysis.Analyze(localNat, remoteNat, ToFlags(report.LocalCandidates), ToFlags(report.RemoteCandidates));
        // ICE got through but DTLS never started: the other side did not apply the answer. Without an ICE path the
        // answering side cannot tell a network problem from an answer that never arrived.
        var message = !answering ? FailureMessage(reason, deviceName)
            : report.Selected is not null
                ? $"No connection with {deviceName}: the answer code was not applied in time."
                : reason == ConnectFailureReason.Unknown
                    ? $"No connection with {deviceName}: the answer code was not applied in time, or the networks did not let the connection through."
                    : FailureMessage(reason, deviceName);
        logger.LogInformation("Internet connection to {Name} failed: {Error} ({Reason}; local {Local}, remote {Remote})",
            deviceName, e.Message, reason, string.Join(",", report.LocalCandidates.Distinct()), string.Join(",", report.RemoteCandidates.Distinct()));
        return (message, reason);
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
            // Closed on purpose by the other side: the device is just offline, no "connection lost".
            if (lost && link.ClosedByPeer)
                _entries.Remove(entry.DeviceId);
            else if (lost)
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
        PairingOffer[] offers;
        PairingLink[] pairings;
        lock (_gate)
        {
            entries = [.. _entries.Values];
            _entries.Clear();
            offers = [.. _pairingOffers.Values];
            _pairingOffers.Clear();
            pairings = [.. _pairingLinks.Values];
            _pairingLinks.Clear();
        }
        foreach (var entry in entries)
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        foreach (var offer in offers)
            await offer.Session.DisposeAsync().ConfigureAwait(false);
        foreach (var pairing in pairings)
            await pairing.Link.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
