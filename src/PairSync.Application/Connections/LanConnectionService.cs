using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PairSync.Application.Pairing;
using PairSync.Application.Presence;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage.Identity;
using PairSync.Storage.Settings;
using PairSync.SyncEngine;
using PairSync.Transport;
using PairSync.Transport.Tls;

namespace PairSync.Application.Connections;

public sealed record LanOptions
{
    /// <summary>Overrides the port from the settings; 0 lets the OS choose (tests, a second instance).</summary>
    public int? Port { get; init; }
}

/// <summary>An authenticated session with another device.</summary>
public sealed class PeerConnection(ITransportSession session, PeerHandshake handshake, PairedDevice? device, bool isIncoming) : IAsyncDisposable
{
    public ITransportSession Session { get; } = session;

    public PeerHandshake Handshake { get; } = handshake;

    public PeerChannels Channels => Handshake.Channels;

    public PeerAccess Access => Handshake.Access;

    /// <summary>The paired device; null for <see cref="PeerAccess.PairingOnly"/>.</summary>
    public PairedDevice? Device { get; } = device;

    public bool IsIncoming { get; } = isIncoming;

    /// <summary>The key the other side proved in the TLS handshake.</summary>
    public byte[] RemotePublicKey => Session.RemotePublicKey ?? [];

    public string RemoteName => Device?.Name ?? Handshake.RemoteName;

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

/// <summary>
/// The LAN side of the connection layer (plan §5, §6, §11): listens on the configured port on all interfaces and
/// connects to other devices. Every session is TLS 1.3 with both device certificates, followed by the Hello
/// handshake in which each side checks the other against its list of paired devices. Sessions over an internet
/// link go through the same handshake (<see cref="ConnectOverAsync"/>, <see cref="AcceptAsync"/>).
/// </summary>
public sealed class LanConnectionService(
    CurrentIdentity identity, SettingsStore settings, PeerAuthorizer authorizer, LanOptions lanOptions, TimeProvider time,
    ILogger<LanConnectionService> logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly TransportOptions _transport = new() { ConnectTimeout = TimeSpan.FromSeconds(15) };
    private X509Certificate2? _certificate;
    private TlsListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;

    /// <summary>Port being listened on; 0 while not listening.</summary>
    public int Port => _listener?.Port ?? 0;

    /// <summary>Why listening failed (port in use, not permitted); null while listening or not started.</summary>
    public string? ListenError { get; private set; }

    /// <summary>
    /// Receives every incoming session after the handshake, paired or pairing-only, and owns it from then on.
    /// Without a handler incoming sessions are closed.
    /// </summary>
    public Func<PeerConnection, Task>? IncomingConnectionHandler { get; set; }

    private X509Certificate2 Certificate => _certificate ??= identity.Value.Identity.CreateCertificate(time);

    private LocalDevice Local => new(identity.Value.Identity.Id, settings.Current.EffectiveDeviceName);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
            return Task.CompletedTask;
        var port = lanOptions.Port ?? settings.Current.Port;
        try
        {
            _listener = TlsListener.Start(port, Certificate, _transport);
            ListenError = null;
        }
        catch (SocketException e)
        {
            ListenError = e.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {port} is already in use. Choose another port in Settings."
                : $"Cannot listen on port {port}: {e.Message}";
            logger.LogError(e, "Cannot listen on port {Port}", port);
            return Task.CompletedTask;
        }

        logger.LogInformation("Listening for devices on port {Port}", _listener.Port);
        _acceptLoop = _listener.RunAsync(SessionHandshake.ChannelLabels, OnSessionAsync,
            (remote, e) => logger.LogDebug(e, "Connection from {Remote} failed during the TLS handshake", remote), _stopping.Token);
        return Task.CompletedTask;
    }

    /// <summary>Connects to a paired device; its certificate must carry the pinned key.</summary>
    /// <exception cref="TransportException">Not reachable, or it presented another key.</exception>
    /// <exception cref="PeerRejectedException">It refused (this device blocked or removed there), or it is blocked here.</exception>
    public async Task<PeerConnection> ConnectAsync(
        PairedDevice device, IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        var session = await TlsConnector.ConnectAsync(addresses, port, Certificate,
            key => key.AsSpan().SequenceEqual(device.PublicKey), SessionHandshake.ChannelLabels, _transport, cancellationToken)
            .ConfigureAwait(false);
        var connection = await InitiateAsync(session, cancellationToken).ConfigureAwait(false);
        if (connection.Access != PeerAccess.Paired)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new PeerRejectedException("the other device no longer knows this one; pair again", byOtherDevice: true);
        }
        return connection;
    }

    /// <summary>
    /// Runs the handshake as the connecting side over a session that is already bound to <paramref name="device"/>'s
    /// key, e.g. one job's channels on an internet link.
    /// </summary>
    /// <exception cref="PeerRejectedException">The other device refused, or no longer knows this one.</exception>
    public async Task<PeerConnection> ConnectOverAsync(ITransportSession session, PairedDevice device, CancellationToken cancellationToken)
    {
        if (session.RemotePublicKey is not { } key || !key.AsSpan().SequenceEqual(device.PublicKey))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new TransportException($"The session is not bound to the key of {device.Name}.");
        }
        var connection = await InitiateAsync(session, cancellationToken).ConfigureAwait(false);
        if (connection.Access != PeerAccess.Paired)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new PeerRejectedException("the other device no longer knows this one; pair again", byOtherDevice: true);
        }
        return connection;
    }

    /// <summary>Opens a pairing session over a session that is already bound to the other device's key (internet pairing).</summary>
    public Task<PeerConnection> ConnectForPairingOverAsync(ITransportSession session, CancellationToken cancellationToken) =>
        InitiateAsync(session, cancellationToken, forPairing: true);

    /// <summary>
    /// Answers the handshake on a session the other side opened (an internet link job) and hands the result to
    /// <see cref="IncomingConnectionHandler"/>, like an incoming LAN connection.
    /// </summary>
    public Task AcceptAsync(ITransportSession session) => OnSessionAsync(session);

    /// <summary>
    /// Opens a pairing session: always <see cref="PeerAccess.PairingOnly"/>, also with a device that is already
    /// known on one or both sides (pairing again after a removal). The key is verified by the security code.
    /// </summary>
    /// <param name="expectedKey">The key from an invitation; null accepts any key (pairing with a device found on the LAN).</param>
    /// <exception cref="TransportException">Not reachable, or it presented another key than <paramref name="expectedKey"/>.</exception>
    /// <exception cref="PeerRejectedException">One side has blocked the other, or its id does not match its key.</exception>
    public async Task<PeerConnection> ConnectForPairingAsync(
        IReadOnlyList<IPAddress> addresses, int port, byte[]? expectedKey, CancellationToken cancellationToken)
    {
        var session = await TlsConnector.ConnectAsync(addresses, port, Certificate,
            key => expectedKey is null || key.AsSpan().SequenceEqual(expectedKey), SessionHandshake.ChannelLabels, _transport,
            cancellationToken).ConfigureAwait(false);
        return await InitiateAsync(session, cancellationToken, forPairing: true).ConfigureAwait(false);
    }

    private async Task<PeerConnection> InitiateAsync(ITransportSession session, CancellationToken cancellationToken, bool forPairing = false)
    {
        try
        {
            PairedDevice? device = null;
            var handshake = await SessionHandshake.InitiateAsync(session, Local, async ack =>
            {
                var result = await authorizer.AuthorizeAsync(session.RemotePublicKey, ack.DeviceId, cancellationToken).ConfigureAwait(false);
                device = result.Decision.Access == PeerAccess.Paired ? result.Device : null;
                return result.Decision;
            }, cancellationToken, forPairing).ConfigureAwait(false);
            logger.LogInformation("Connected to {Name} ({Access})", device?.Name ?? handshake.RemoteName, handshake.Access);
            return new PeerConnection(session, handshake, handshake.Access == PeerAccess.Paired ? device : null, isIncoming: false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task OnSessionAsync(ITransportSession session)
    {
        PeerConnection connection;
        try
        {
            PairedDevice? device = null;
            var handshake = await SessionHandshake.RespondAsync(session, Local, async hello =>
            {
                var result = await authorizer.AuthorizeAsync(session.RemotePublicKey, hello.DeviceId, _stopping.Token).ConfigureAwait(false);
                if (result.Decision.Access is null)
                {
                    logger.LogInformation("Refused {Name} from {Remote}: {Reason}",
                        result.Device?.Name ?? hello.DeviceName, session.Route?.RemoteAddress, result.Decision.RejectReason);
                    return result.Decision;
                }
                if (hello.Pairing)
                    return AccessDecision.Grant(PeerAccess.PairingOnly);
                device = result.Decision.Access == PeerAccess.Paired ? result.Device : null;
                return result.Decision;
            }, _stopping.Token).ConfigureAwait(false);
            connection = new PeerConnection(session, handshake, device, isIncoming: true);
        }
        catch (Exception e) when (e is ProtocolException or TransportException or TransferCanceledException or OperationCanceledException)
        {
            if (e is not PeerRejectedException)
                logger.LogDebug(e, "Handshake with {Remote} failed", session.Route?.RemoteAddress);
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        logger.LogInformation("Incoming connection from {Name} ({Access})", connection.RemoteName, connection.Access);
        if (IncomingConnectionHandler is { } handler)
            await handler(connection).ConfigureAwait(false);
        else
            await connection.DisposeAsync().ConfigureAwait(false);
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Called by PairSyncCore while the service provider still works, and again when the provider is disposed.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _certificate?.Dispose();
        _stopping.Dispose();
    }
}

public static class ConnectionServices
{
    public static IServiceCollection AddPairSyncConnections(this IServiceCollection services)
    {
        services.AddSingleton(new LanOptions());
        services.AddSingleton<PeerAuthorizer>();
        services.AddSingleton<LanConnectionService>();
        services.AddSingleton(new Internet.InternetOptions());
        services.AddSingleton<Internet.AnsweredOffers>();
        services.AddSingleton<Internet.InternetLinkService>();
        services.AddSingleton<PeerLinks>();
        services.AddSingleton(new PresenceOptions());
        services.AddSingleton<PresenceService>();
        services.AddSingleton(new PairingOptions());
        services.AddSingleton<PairingService>();
        services.AddSingleton(new TransferOptions());
        services.AddSingleton<TransferService>();
        services.AddSingleton<Devices.DeviceService>();
        services.AddSingleton<Sync.SyncIndex>();
        services.AddSingleton(new Sync.SyncOptions());
        services.AddSingleton<Sync.SyncService>();
        return services;
    }
}
