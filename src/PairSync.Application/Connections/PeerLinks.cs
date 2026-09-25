using Microsoft.Extensions.Logging;
using PairSync.Application.Internet;
using PairSync.Application.Presence;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Transport;

namespace PairSync.Application.Connections;

/// <summary>How a device can be reached right now.</summary>
public enum PeerRoute
{
    None,

    /// <summary>Seen on the LAN via mDNS: TLS over TCP, fast.</summary>
    Lan,

    /// <summary>An internet link is open (WebRTC, slower).</summary>
    Internet,
}

/// <summary>
/// Picks the way to a paired device for a job (phase 2 block C): the LAN whenever mDNS sees the device, since TLS
/// over TCP is far faster than WebRTC; otherwise an open internet link; otherwise the job waits (plan §12).
/// </summary>
public sealed class PeerLinks
{
    private readonly PresenceService _presence;
    private readonly LanConnectionService _lan;
    private readonly InternetLinkService _internet;
    private readonly ILogger<PeerLinks> _logger;

    public PeerLinks(PresenceService presence, LanConnectionService lan, InternetLinkService internet, ILogger<PeerLinks> logger)
    {
        _presence = presence;
        _lan = lan;
        _internet = internet;
        _logger = logger;
        presence.PairedDeviceAvailable += (_, _) => DeviceAvailable?.Invoke();
        internet.LinkEstablished += _ => DeviceAvailable?.Invoke();
    }

    public PeerRoute RouteTo(Guid deviceId) =>
        _presence.FindEndpoint(deviceId) is not null ? PeerRoute.Lan
        : _internet.LinkTo(deviceId) is not null ? PeerRoute.Internet
        : PeerRoute.None;

    public bool IsReachable(Guid deviceId) => RouteTo(deviceId) != PeerRoute.None;

    /// <summary>A paired device became reachable, on the LAN or over a new internet link.</summary>
    public event Action? DeviceAvailable;

    /// <summary>Connects to a paired device for a job: LAN first, then the internet link.</summary>
    /// <exception cref="TransportException">Not reachable either way.</exception>
    /// <exception cref="PeerRejectedException">The other device refused.</exception>
    public async Task<PeerConnection> ConnectAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        if (_presence.FindEndpoint(device.Id) is { } endpoint)
        {
            try
            {
                return await _lan.ConnectAsync(device, endpoint.Addresses, endpoint.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (TransportException e) when (_internet.LinkTo(device.Id) is not null)
            {
                _logger.LogDebug(e, "LAN connection to {Name} failed, using the internet link", device.Name);
            }
        }

        if (_internet.LinkTo(device.Id) is { } link)
        {
            var session = await link.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            return await _lan.ConnectOverAsync(session, device, cancellationToken).ConfigureAwait(false);
        }

        throw new TransportException($"{device.Name} is not reachable.");
    }
}
