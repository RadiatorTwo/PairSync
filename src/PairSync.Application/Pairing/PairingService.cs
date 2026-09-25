using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Application.Presence;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.Storage.Identity;
using PairSync.Storage.Settings;
using PairSync.SyncEngine;
using PairSync.Transport;
using PairSync.Transport.Tls;

namespace PairSync.Application.Pairing;

public sealed record PairingOptions
{
    /// <summary>Plan §5: invitations are short-lived and single use.</summary>
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long both users have to compare and confirm the security code.</summary>
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Clock difference tolerated when checking another device's invitation.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Addresses written into invitations instead of those of the network interfaces (tests: loopback).</summary>
    public IReadOnlyList<IPAddress>? InvitationAddresses { get; init; }
}

/// <summary>
/// Pairing (plan §5, work package E) in two ways with the same end: with a device found on the LAN, or with a
/// signed invitation. Either way both devices derive the security code by commit-reveal over the pairing session,
/// both users confirm it, and only then each device stores the other's key.
/// </summary>
public sealed class PairingService(
    CurrentIdentity identity, SettingsStore settings, LanConnectionService lan, PresenceService presence,
    IDbContextFactory<PairSyncDbContext> contexts, PairingOptions options, TimeProvider time, ILogger<PairingService> logger)
    : IAsyncDisposable
{
    /// <summary>For the exchange of commitment and nonces, which needs no user.</summary>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);

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

    /// <summary>The user closed the invitation; it can no longer be used.</summary>
    public void RevokeInvitation(IssuedInvitation invitation) => _invitations.Revoke(invitation.Nonce);

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
        PairAsync(invitation.DeviceId, invitation.Addresses, invitation.Port, invitation.PublicKey, invitation.Nonce, cancellationToken);

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

    private async Task<PairingSession> PairAsync(
        Guid deviceId, IReadOnlyList<IPAddress> addresses, int port, byte[]? expectedKey, byte[]? invitationNonce,
        CancellationToken cancellationToken)
    {
        lock (_gate)
            _inProgress++;
        PeerConnection? connection = null;
        try
        {
            connection = await lan.ConnectForPairingAsync(addresses, port, expectedKey, cancellationToken).ConfigureAwait(false);
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
        session.Completion.ContinueWith(_ =>
        {
            lock (_gate)
            {
                _sessions.Remove(session);
                _inProgress--;
            }
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
            return device;
        }
        catch (DbUpdateException e)
        {
            throw new PairingException("The paired device could not be saved.", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        PairingSession[] sessions;
        lock (_gate)
            sessions = [.. _sessions];
        foreach (var session in sessions)
            await session.CancelAsync("PairSync was closed").ConfigureAwait(false);
        _stopping.Dispose();
    }
}
