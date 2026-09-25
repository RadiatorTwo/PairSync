using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Application.Internet;
using PairSync.Application.Presence;
using PairSync.Discovery.Lan;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.Storage.Identity;
using PairSync.Storage.Settings;
using PairSync.Stun;
using PairSync.SyncEngine;
using PairSync.Transport;
using PairSync.Transport.Tls;

namespace PairSync.Application.Pairing;

public sealed record PairingOptions
{
    /// <summary>Plan §5: invitations are short-lived and single use.</summary>
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>An internet invitation needs a round trip of codes (invitation, answer), like a connection code.</summary>
    public TimeSpan InternetInvitationLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a TCP connect to an invitation's LAN address may take before the internet path is used instead.</summary>
    public TimeSpan LanProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long both users have to compare and confirm the security code.</summary>
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Clock difference tolerated when checking another device's invitation.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Addresses written into invitations instead of those of the network interfaces (tests: loopback).</summary>
    public IReadOnlyList<IPAddress>? InvitationAddresses { get; init; }
}

/// <summary>
/// How an invitation is taken up (<see cref="PairingService.AcceptInvitationAsync"/>): over the LAN right away, or
/// over the internet, where <see cref="Answer"/> has to go back to the inviting device first.
/// </summary>
/// <param name="Answer">The answer code (<c>PSR1</c>) to send back; null when pairing runs over the LAN.</param>
/// <param name="Session">Completes once both devices show the security code.</param>
public sealed record InvitationPairing(IssuedCode? Answer, Task<PairingSession> Session);

/// <summary>
/// Pairing (plan §5, work package E) in two ways with the same end: with a device found on the LAN, or with a
/// signed invitation. Either way both devices derive the security code by commit-reveal over the pairing session,
/// both users confirm it, and only then each device stores the other's key. An internet invitation (phase 2
/// block D) runs the same pairing session over a WebRTC connection set up by invitation and answer code; once
/// paired, that connection stays open as the device's internet link.
/// </summary>
public sealed class PairingService(
    CurrentIdentity identity, SettingsStore settings, LanConnectionService lan, PresenceService presence, InternetLinkService internet,
    IDbContextFactory<PairSyncDbContext> contexts, PairingOptions options, TimeProvider time, ILogger<PairingService> logger)
    : IAsyncDisposable
{
    /// <summary>For the exchange of commitment and nonces, which needs no user.</summary>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>After its answer was applied, an internet invitation stays redeemable this long for the pairing request.</summary>
    private static readonly TimeSpan AnsweredInvitationGrace = TimeSpan.FromMinutes(2);

    private readonly InvitationBook _invitations = new(time);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly HashSet<PairingSession> _sessions = [];
    private int _inProgress;

    /// <summary>
    /// Another device wants to pair and both screens can now show the code. Raised on a background thread; the
    /// handler owns the session. Without a handler, incoming pairing requests are refused.
    /// </summary>
    public event Action<PairingSession>? IncomingRequest;

    private byte[] LocalKey => identity.Value.Identity.PublicKey;

    /// <summary>A new signed invitation for this device, valid for <see cref="PairingOptions.InvitationLifetime"/>.</summary>
    /// <exception cref="PairingException">Not listening, or no network address to put into it.</exception>
    public IssuedInvitation CreateInvitation()
    {
        if (lan.Port == 0)
            throw new PairingException(lan.ListenError ?? "This device is not listening for connections.");
        var addresses = options.InvitationAddresses ?? TlsEndpointInfo.LocalAddresses();
        if (addresses.Count == 0)
            throw new PairingException("No network connection: the invitation would contain no address to connect to.");

        var nonce = RandomNumberGenerator.GetBytes(PairingInvitations.NonceSize);
        var expires = time.GetUtcNow().UtcDateTime + options.InvitationLifetime;
        var text = PairingInvitations.Create(identity.Value.Identity, settings.Current.EffectiveDeviceName, addresses, lan.Port, nonce, expires);
        _invitations.Add(nonce, expires);
        logger.LogInformation("Created a pairing invitation for {Addresses} port {Port}, valid until {Expires:u}",
            string.Join(", ", addresses), lan.Port, expires);
        return new IssuedInvitation(text, expires, nonce);
    }

    /// <summary>
    /// A new invitation; with <paramref name="overInternet"/> (default: <see cref="InternetLinkService.CanInviteOverInternet"/>)
    /// an internet invitation (<c>PSI2</c>) that also carries a WebRTC offer, valid for
    /// <see cref="PairingOptions.InternetInvitationLifetime"/>. It still lists the LAN addresses, if this device listens.
    /// </summary>
    /// <exception cref="PairingException">Neither a LAN address nor an internet offer could be put into it.</exception>
    public async Task<IssuedInvitation> CreateInvitationAsync(CancellationToken cancellationToken, bool? overInternet = null)
    {
        if (!(overInternet ?? internet.CanInviteOverInternet))
            return CreateInvitation();

        IReadOnlyList<IPAddress> addresses = lan.Port == 0 ? [] : options.InvitationAddresses ?? TlsEndpointInfo.LocalAddresses();
        var nonce = RandomNumberGenerator.GetBytes(PairingInvitations.NonceSize);
        var expires = time.GetUtcNow().UtcDateTime + options.InternetInvitationLifetime;
        SessionDescription offer;
        NatHint nat;
        try
        {
            (offer, nat) = await internet.CreatePairingOfferAsync(nonce, expires, cancellationToken).ConfigureAwait(false);
        }
        catch (InternetConnectException e)
        {
            throw new PairingException(e.Message, e);
        }
        var text = PairingInvitations.Create(identity.Value.Identity, settings.Current.EffectiveDeviceName, addresses, lan.Port, nonce, expires,
            offer, nat);
        _invitations.Add(nonce, expires);
        logger.LogInformation("Created an internet pairing invitation (LAN {Addresses} port {Port}), valid until {Expires:u}",
            addresses.Count == 0 ? "none" : string.Join(", ", addresses), lan.Port, expires);
        return new IssuedInvitation(text, expires, nonce);
    }

    /// <summary>The user closed the invitation; it can no longer be used.</summary>
    public void RevokeInvitation(IssuedInvitation invitation)
    {
        _invitations.Revoke(invitation.Nonce);
        internet.RevokePairingOffer(invitation.Nonce);
    }

    /// <summary>
    /// Takes up an invitation on the invited device: over the LAN if the inviting device answers there (mDNS or its
    /// invitation addresses), otherwise, for an internet invitation, by answering its offer. The answer code then has
    /// to reach the inviting device (<see cref="ApplyInvitationAnswerAsync"/>) before the session can start.
    /// </summary>
    /// <param name="cancellationToken">Also ends the wait for the other device to apply the answer.</param>
    /// <exception cref="PairingException">Not reachable over the LAN and no internet offer, or the answer cannot be created.</exception>
    public async Task<InvitationPairing> AcceptInvitationAsync(ReceivedInvitation invitation, CancellationToken cancellationToken)
    {
        var lanInvitation = presence.FindEndpoint(invitation.DeviceId) is { } endpoint
            ? invitation with { Addresses = endpoint.Addresses, Port = endpoint.Port }
            : await AcceptsConnectionsAsync(invitation.Addresses, invitation.Port, cancellationToken).ConfigureAwait(false) ? invitation : null;
        if (lanInvitation is not null)
        {
            try
            {
                return new InvitationPairing(null,
                    Task.FromResult(await PairAsync(lanInvitation, markUnreachable: true, cancellationToken).ConfigureAwait(false)));
            }
            catch (PairingUnreachableException e) when (invitation.Offer is not null)
            {
                logger.LogInformation(e, "{Name} is not reachable on the LAN, pairing over the internet", invitation.DeviceName);
            }
        }
        if (invitation.Offer is null)
            return new InvitationPairing(null, Task.FromResult(await PairAsync(invitation, cancellationToken).ConfigureAwait(false)));

        IssuedCode answer;
        Task<InternetLink> connected;
        try
        {
            (answer, connected) = await internet.AnswerInvitationAsync(invitation, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidConnectCodeException e)
        {
            throw new InvalidInvitationException(e.Message, e);
        }
        catch (InternetConnectException e)
        {
            throw new PairingException(e.Message, e);
        }
        return new InvitationPairing(answer, PairOverInternetAsync(invitation, connected, cancellationToken));
    }

    /// <summary>
    /// On the inviting device: applies the answer code of the invited device and waits until the connection is up.
    /// The invited device then starts the pairing, which arrives as <see cref="IncomingRequest"/> like over the LAN.
    /// </summary>
    /// <returns>Who answered; the security code confirms it.</returns>
    /// <exception cref="InvalidInvitationException">Not a valid answer, or not for an open invitation of this device.</exception>
    /// <exception cref="PairingException">No connection came up; an inner <see cref="InternetConnectException"/> says why.</exception>
    public async Task<ReceivedAnswer> ApplyInvitationAnswerAsync(string text, CancellationToken cancellationToken)
    {
        ReceivedAnswer answer;
        try
        {
            answer = ConnectCodes.ReadAnswer(text, time.GetUtcNow().UtcDateTime, options.ClockSkew, identity.Value.Identity.Id);
        }
        catch (InvalidConnectCodeException e)
        {
            throw new InvalidInvitationException(e.Message, e);
        }
        // The pairing request follows over the new connection; it may arrive after the invitation itself expired.
        if (!_invitations.TryExtend(answer.OfferNonce, time.GetUtcNow().UtcDateTime + AnsweredInvitationGrace))
            throw new InvalidInvitationException(
                "This answer code does not belong to an open invitation of this device. It may have expired or been used already.");

        try
        {
            await internet.ConnectInvitationAsync(answer, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidConnectCodeException e)
        {
            // Another answer took the offer already; that pairing keeps the invitation.
            throw new InvalidInvitationException(e.Message, e);
        }
        catch (Exception e)
        {
            // The offer is used up, so the invitation is as well.
            _invitations.Revoke(answer.OfferNonce);
            throw e switch
            {
                InternetConnectException => new PairingException(e.Message, e),
                TransportException => new InvalidInvitationException($"The answer code cannot be used: {e.Message}", e),
                _ => e,
            };
        }
        logger.LogInformation("Connected to {Name} over the internet for pairing", answer.DeviceName);
        return answer;
    }

    /// <summary>The invited device waits for the connection, then pairs over it as it would over the LAN.</summary>
    private async Task<PairingSession> PairOverInternetAsync(
        ReceivedInvitation invitation, Task<InternetLink> connected, CancellationToken cancellationToken)
    {
        InternetLink link;
        try
        {
            link = await connected.ConfigureAwait(false);
        }
        catch (InternetConnectException e)
        {
            throw new PairingException(e.Message, e);
        }
        try
        {
            return await PairAsync(invitation.DeviceId, async ct =>
            {
                var session = await link.OpenSessionAsync(ct).ConfigureAwait(false);
                return await lan.ConnectForPairingOverAsync(session, ct).ConfigureAwait(false);
            }, invitation.Nonce, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await internet.DiscardPairingLinkAsync(invitation.DeviceId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Whether something accepts TCP connections at one of these addresses, within <see cref="PairingOptions.LanProbeTimeout"/>.</summary>
    private async Task<bool> AcceptsConnectionsAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0 || port == 0)
            return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.LanProbeTimeout);
        var results = await Task.WhenAll(addresses.Select(async address =>
        {
            try
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                return false;
            }
        })).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results.Any(r => r);
    }

    /// <exception cref="InvalidInvitationException">With a message for the user.</exception>
    public ReceivedInvitation ReadInvitation(string text) =>
        PairingInvitations.Read(text, time.GetUtcNow().UtcDateTime, options.ClockSkew, identity.Value.Identity.Id);

    /// <summary>Reads a <c>.pairsync-invite</c> file.</summary>
    /// <exception cref="InvalidInvitationException">Not readable, or not a valid invitation.</exception>
    public async Task<ReceivedInvitation> ReadInvitationFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path).Length > InvitationCodec.MaxTextLength)
                throw new InvalidInvitationException("The file is too large to be a PairSync invitation.");
            return ReadInvitation(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidInvitationException($"The file cannot be read: {e.Message}", e);
        }
    }

    /// <summary>Connects to the inviting device and starts pairing; returns once both sides can show the code.</summary>
    /// <exception cref="PairingException">Not reachable, invitation refused, or the other device answered with another key.</exception>
    public Task<PairingSession> PairAsync(ReceivedInvitation invitation, CancellationToken cancellationToken) =>
        PairAsync(invitation, markUnreachable: false, cancellationToken);

    /// <param name="markUnreachable">Throw <see cref="PairingUnreachableException"/> if the device cannot be reached at all.</param>
    private async Task<PairingSession> PairAsync(ReceivedInvitation invitation, bool markUnreachable, CancellationToken cancellationToken)
    {
        var session = await PairAsync(invitation.DeviceId, invitation.Addresses, invitation.Port, invitation.PublicKey, invitation.Nonce,
            cancellationToken, markUnreachable).ConfigureAwait(false);
        // The invitation says where the device listens; that also works where mDNS is blocked.
        _ = session.Completion.ContinueWith(_ => presence.AddKnownEndpoint(new LanServiceInfo(
                invitation.DeviceId, ProtocolVersion.Current, invitation.Port, invitation.Addresses, time.GetUtcNow().UtcDateTime)),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        return session;
    }

    /// <summary>"Pair…" on a device found on the LAN.</summary>
    /// <exception cref="PairingException">Not reachable, or it refused.</exception>
    public Task<PairingSession> PairAsync(NearbyDevice device, CancellationToken cancellationToken) =>
        device.Lan is { } lanInfo
            ? PairAsync(device.Id, lanInfo.Addresses, lanInfo.Port, cancellationToken)
            : throw new PairingException($"{device.Name} is not on the LAN right now.");

    /// <summary>Pairs with the device that announced <paramref name="deviceId"/> at these addresses.</summary>
    /// <exception cref="PairingException">Not reachable, it refused, or another device answered.</exception>
    public Task<PairingSession> PairAsync(Guid deviceId, IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken) =>
        PairAsync(deviceId, addresses, port, expectedKey: null, invitationNonce: null, cancellationToken);

    private Task<PairingSession> PairAsync(
        Guid deviceId, IReadOnlyList<IPAddress> addresses, int port, byte[]? expectedKey, byte[]? invitationNonce,
        CancellationToken cancellationToken, bool markUnreachable = false) =>
        PairAsync(deviceId, async ct =>
        {
            try
            {
                return await lan.ConnectForPairingAsync(addresses, port, expectedKey, ct).ConfigureAwait(false);
            }
            catch (TransportException e) when (markUnreachable)
            {
                throw new PairingUnreachableException(e.Message, e);
            }
        }, invitationNonce, cancellationToken);

    /// <summary>Connects with <paramref name="connect"/> and runs the commit-reveal exchange as the initiating side.</summary>
    private async Task<PairingSession> PairAsync(
        Guid deviceId, Func<CancellationToken, Task<PeerConnection>> connect, byte[]? invitationNonce, CancellationToken cancellationToken)
    {
        lock (_gate)
            _inProgress++;
        PeerConnection? connection = null;
        try
        {
            connection = await connect(cancellationToken).ConfigureAwait(false);
            if (connection.Handshake.RemoteDeviceId != deviceId)
                throw new PairingException("Another device answered at this address. Refresh the device list and try again.");
            if (await ConflictAsync(connection, cancellationToken).ConfigureAwait(false) is { } conflict)
                throw new PairingException(conflict);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ExchangeTimeout);
            var control = connection.Channels.Control;
            var nonce = SecurityCode.CreateNonce();
            await control.SendAsync(new PairCommit { Commitment = SecurityCode.Commit(nonce), InvitationNonce = invitationNonce }, timeout.Token)
                .ConfigureAwait(false);
            var answer = await control.ExpectAsync<PairNonce>(timeout.Token).ConfigureAwait(false);
            if (answer.Nonce is not { Length: SecurityCode.NonceSize } remoteNonce)
                throw new PairingException("The other device does not follow the pairing protocol.");
            await control.SendAsync(new PairReveal { Nonce = nonce }, timeout.Token).ConfigureAwait(false);

            var code = SecurityCode.Derive(LocalKey, connection.RemotePublicKey, nonce, remoteNonce);
            return StartSession(connection, code, viaInvitation: invitationNonce is not null);
        }
        catch (Exception e)
        {
            Release();
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
            throw e switch
            {
                PairingException => e,
                OperationCanceledException when cancellationToken.IsCancellationRequested => e,
                TransferCanceledException canceled => new PairingCanceledException(canceled.Reason, byOtherDevice: true),
                OperationCanceledException => new PairingException("The other device did not answer.", e),
                TransportException or ProtocolException => new PairingException(e.Message, e),
                _ => e,
            };
        }
    }

    /// <summary>
    /// Takes over an incoming <see cref="PeerAccess.PairingOnly"/> session: answers the commitment, checks the
    /// invitation if one is redeemed, and hands the session to <see cref="IncomingRequest"/>.
    /// </summary>
    public async Task HandleIncomingAsync(PeerConnection connection)
    {
        var reserved = false;
        var owned = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            timeout.CancelAfter(ExchangeTimeout);
            var control = connection.Channels.Control;
            var commit = await control.ExpectAsync<PairCommit>(timeout.Token).ConfigureAwait(false);

            string? refusal;
            (refusal, reserved) = TryReserveIncoming(commit);
            refusal ??= await ConflictAsync(connection, timeout.Token).ConfigureAwait(false);
            if (refusal is null && commit.InvitationNonce is { } invitationNonce && !_invitations.TryRedeem(invitationNonce))
                refusal = "the invitation has expired or was already used";
            if (refusal is not null)
            {
                logger.LogInformation("Refused to pair with {Name}: {Reason}", connection.RemoteName, refusal);
                await control.SendAsync(new Cancel { Reason = refusal }, timeout.Token).ConfigureAwait(false);
                return;
            }

            var nonce = SecurityCode.CreateNonce();
            await control.SendAsync(new PairNonce { Nonce = nonce }, timeout.Token).ConfigureAwait(false);
            var reveal = await control.ExpectAsync<PairReveal>(timeout.Token).ConfigureAwait(false);
            if (reveal.Nonce is not { Length: SecurityCode.NonceSize } remoteNonce ||
                !SecurityCode.MatchesCommitment(commit.Commitment, remoteNonce))
            {
                logger.LogWarning("{Name} revealed a nonce that does not match its commitment", connection.RemoteName);
                await control.SendAsync(new Cancel { Reason = "the pairing protocol was violated" }, timeout.Token).ConfigureAwait(false);
                return;
            }

            var code = SecurityCode.Derive(LocalKey, connection.RemotePublicKey, remoteNonce, nonce);
            var session = StartSession(connection, code, viaInvitation: commit.InvitationNonce is not null);
            reserved = owned = false; // the session owns both now
            logger.LogInformation("{Name} wants to pair ({Via})", session.RemoteName, session.ViaInvitation ? "invitation" : "LAN");
            IncomingRequest?.Invoke(session);
        }
        catch (Exception e) when (e is TransportException or ProtocolException or TransferCanceledException or OperationCanceledException)
        {
            logger.LogDebug(e, "Incoming pairing with {Name} failed", connection.RemoteName);
        }
        finally
        {
            if (reserved)
                Release();
            if (owned)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One pairing at a time: a second request, e.g. from a stranger on the LAN, does not pile up dialogs.</summary>
    private (string? Refusal, bool Reserved) TryReserveIncoming(PairCommit commit)
    {
        if (commit.Commitment is not { Length: SHA256.HashSizeInBytes })
            return ("the pairing protocol was violated", false);
        if (IncomingRequest is null)
            return ("the other device is not ready to pair", false);
        lock (_gate)
        {
            if (_inProgress > 0)
                return ("the other device is already pairing with another device; try again in a moment", false);
            _inProgress++;
            return (null, true);
        }
    }

    private void Release()
    {
        lock (_gate)
            _inProgress--;
    }

    /// <summary>A known device id with another key is never replaced silently; the user removes the old entry first.</summary>
    private async Task<string?> ConflictAsync(PeerConnection connection, CancellationToken cancellationToken)
    {
        var id = connection.Handshake.RemoteDeviceId;
        var key = connection.RemotePublicKey;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Devices.AnyAsync(d => d.Id == id && d.PublicKey != key, cancellationToken).ConfigureAwait(false)
            ? "a paired device already uses this device id with another key; remove it first"
            : null;
    }

    private PairingSession StartSession(PeerConnection connection, string code, bool viaInvitation)
    {
        var session = new PairingSession(connection, code, viaInvitation, options.ConfirmationTimeout, time, StoreAsync, logger);
        lock (_gate)
            _sessions.Add(session);
        // Over an internet pairing link the session decides whether the link stays (paired) or closes.
        internet.ClaimPairingLink(session.RemoteDeviceId);
        session.Completion.ContinueWith(completion =>
        {
            lock (_gate)
            {
                _sessions.Remove(session);
                _inProgress--;
            }
            if (!completion.IsCompletedSuccessfully)
                _ = internet.DiscardPairingLinkAsync(session.RemoteDeviceId);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return session;
    }

    /// <summary>Plan §5, step 6: both confirmed, so this device now trusts the other one's key.</summary>
    private async Task<PairedDevice> StoreAsync(PairingSession session)
    {
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            await using var db = await contexts.CreateDbContextAsync().ConfigureAwait(false);
            var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == session.RemoteDeviceId).ConfigureAwait(false);
            if (device is null)
            {
                device = new PairedDevice { Id = session.RemoteDeviceId, PublicKey = session.RemotePublicKey };
                db.Devices.Add(device);
            }
            else if (!device.PublicKey.AsSpan().SequenceEqual(session.RemotePublicKey))
            {
                throw new PairingException("A paired device already uses this device id with another key. Remove it first.");
            }

            // Pairing again (after a removal on the other side) keeps the permissions set here.
            device.Name = session.RemoteName;
            device.PairedAtUtc = now;
            device.LastSeenUtc = now;
            device.Trust = DeviceTrust.Active;
            await db.SaveChangesAsync().ConfigureAwait(false);
            await presence.ReloadPairedDevicesAsync(CancellationToken.None).ConfigureAwait(false);
            // Paired over the internet: the connection stays open as the device's internet link.
            await internet.AdoptPairingLinkAsync(device).ConfigureAwait(false);
            return device;
        }
        catch (DbUpdateException e)
        {
            throw new PairingException("The paired device could not be saved.", e);
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Called by PairSyncCore while the service provider still works, and again when the provider is disposed.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        PairingSession[] sessions;
        lock (_gate)
            sessions = [.. _sessions];
        foreach (var session in sessions)
            await session.CancelAsync("PairSync was closed").ConfigureAwait(false);
        _stopping.Dispose();
    }
}
