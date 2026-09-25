using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Discovery.Lan;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.Storage.Identity;

namespace PairSync.Application.Presence;

public enum PresenceState
{
    /// <summary>Paired and currently announced on the LAN.</summary>
    Online = 0,

    /// <summary>Paired, not seen right now ("Last seen …").</summary>
    Offline = 1,

    /// <summary>On the LAN, not paired: "Found on the LAN. Not paired, no access."</summary>
    Found = 2,
}

/// <summary>One card of the device grid in Overview/Devices.</summary>
public sealed record NearbyDevice(
    Guid Id, string Name, PresenceState State, DeviceTrust? Trust, DateTime? LastSeenUtc, LanServiceInfo? Lan)
{
    public bool IsPaired => State != PresenceState.Found;

    /// <summary>Unpaired devices publish no name; the mockup shows them as <c>device-7F2A</c>.</summary>
    public static string FoundName(Guid id) => "device-" + id.ToString("N")[..4].ToUpperInvariant();
}

public sealed record PresenceOptions
{
    /// <summary>Off in most tests: every core would announce itself on the real network.</summary>
    public bool Enabled { get; init; } = true;

    public TimeSpan QueryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Expiry { get; init; } = TimeSpan.FromSeconds(95);

    public bool IncludeLoopback { get; init; }
}

/// <summary>
/// Combines the paired devices from the database with what mDNS sees (plan §6, §12): paired devices are online with
/// an address or offline with their last-seen time; others are "found". A paired device that comes online raises
/// <see cref="PairedDeviceAvailable"/>, so waiting jobs can start.
/// </summary>
public sealed class PresenceService(
    IDbContextFactory<PairSyncDbContext> contexts, CurrentIdentity identity, LanConnectionService lan, PresenceOptions options,
    TimeProvider time, ILoggerFactory loggers) : IAsyncDisposable
{
    private readonly ILogger _logger = loggers.CreateLogger<PresenceService>();
    private readonly Lock _gate = new();
    private Dictionary<Guid, PairedDevice> _paired = [];
    private readonly Dictionary<Guid, LanServiceInfo> _online = [];
    private LanDiscovery? _discovery;

    /// <summary>The device list changed; read <see cref="Devices"/> again.</summary>
    public event Action? Changed;

    /// <summary>A paired, not blocked device became reachable on the LAN.</summary>
    public event Action<PairedDevice, LanServiceInfo>? PairedDeviceAvailable;

    /// <summary>Why LAN discovery is off (mDNS blocked, not listening); null when running.</summary>
    public string? DiscoveryError { get; private set; }

    /// <summary>Paired devices by name, then found ones.</summary>
    public IReadOnlyList<NearbyDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                var paired = _paired.Values.Select(d => _online.TryGetValue(d.Id, out var info)
                        ? new NearbyDevice(d.Id, d.Name, PresenceState.Online, d.Trust, info.LastSeenUtc, info)
                        : new NearbyDevice(d.Id, d.Name, PresenceState.Offline, d.Trust, d.LastSeenUtc, null))
                    .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);
                var found = _online.Values.Where(i => !_paired.ContainsKey(i.DeviceId))
                    .Select(i => new NearbyDevice(i.DeviceId, NearbyDevice.FoundName(i.DeviceId), PresenceState.Found, null, i.LastSeenUtc, i))
                    .OrderBy(d => d.Name, StringComparer.Ordinal);
                return [.. paired, .. found];
            }
        }
    }

    /// <summary>Where a device was last announced, if it is online.</summary>
    public LanServiceInfo? FindEndpoint(Guid deviceId)
    {
        lock (_gate)
            return _online.GetValueOrDefault(deviceId);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReloadPairedDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (!options.Enabled)
            return;
        if (lan.Port == 0)
        {
            DiscoveryError = "Not listening for connections, so this device is not announced on the LAN.";
            return;
        }

        _discovery = new LanDiscovery(new LanDiscoveryOptions
        {
            DeviceId = identity.Value.Identity.Id,
            Port = lan.Port,
            ProtocolVersion = ProtocolVersion.Current,
            QueryInterval = options.QueryInterval,
            Expiry = options.Expiry,
            IncludeLoopback = options.IncludeLoopback,
        }, time, loggers.CreateLogger<LanDiscovery>());
        _discovery.Seen += OnSeen;
        _discovery.Lost += OnLost;
        _discovery.Start();
        DiscoveryError = _discovery.Error;
    }

    /// <summary>Re-reads the paired devices; call after pairing, blocking or removing a device.</summary>
    public async Task ReloadPairedDevicesAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var devices = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.Id, cancellationToken).ConfigureAwait(false);
        List<(PairedDevice, LanServiceInfo)> available;
        lock (_gate)
        {
            available = [.. devices.Values
                .Where(d => d.Trust == DeviceTrust.Active && !_paired.ContainsKey(d.Id) && _online.ContainsKey(d.Id))
                .Select(d => (d, _online[d.Id]))];
            _paired = devices;
        }
        foreach (var (device, info) in available)
            PairedDeviceAvailable?.Invoke(device, info);
        Changed?.Invoke();
    }

    /// <summary>Announces again and queries now, e.g. when the user opens the device list.</summary>
    public void Refresh() => _discovery?.Refresh();

    private void OnSeen(LanServiceInfo info)
    {
        PairedDevice? cameOnline = null;
        lock (_gate)
        {
            var wasOnline = _online.ContainsKey(info.DeviceId);
            _online[info.DeviceId] = info;
            if (!wasOnline && _paired.TryGetValue(info.DeviceId, out var device) && device.Trust == DeviceTrust.Active)
                cameOnline = device;
        }
        if (cameOnline is not null)
        {
            _logger.LogInformation("{Name} is online at {Addresses} port {Port}", cameOnline.Name, string.Join(", ", info.Addresses), info.Port);
            PairedDeviceAvailable?.Invoke(cameOnline, info);
        }
        Changed?.Invoke();
    }

    private void OnLost(Guid deviceId)
    {
        LanServiceInfo? last;
        PairedDevice? device;
        lock (_gate)
        {
            _online.Remove(deviceId, out last);
            if (_paired.TryGetValue(deviceId, out device) && last is not null)
                device.LastSeenUtc = last.LastSeenUtc;
        }
        if (device is not null && last is not null)
        {
            _logger.LogInformation("{Name} went offline", device.Name);
            _ = SaveLastSeenAsync(deviceId, last.LastSeenUtc);
        }
        Changed?.Invoke();
    }

    private async Task SaveLastSeenAsync(Guid deviceId, DateTime lastSeenUtc)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync().ConfigureAwait(false);
            await db.Devices.Where(d => d.Id == deviceId && (d.LastSeenUtc == null || d.LastSeenUtc < lastSeenUtc))
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.LastSeenUtc, lastSeenUtc)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(e, "Could not store last-seen time of {DeviceId}", deviceId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_discovery is null)
            return;
        _discovery.Seen -= OnSeen;
        _discovery.Lost -= OnLost;
        _discovery.Dispose(); // says goodbye on the LAN
        List<LanServiceInfo> online;
        lock (_gate)
            online = [.. _online.Values.Where(i => _paired.ContainsKey(i.DeviceId))];
        foreach (var info in online)
            await SaveLastSeenAsync(info.DeviceId, info.LastSeenUtc).ConfigureAwait(false);
    }
}
