using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace PairSync.Discovery.Lan;

/// <summary>A PairSync instance seen on the LAN. Everything here is claimed, not proven: the key check happens on connect.</summary>
public sealed record LanServiceInfo(Guid DeviceId, string ProtocolVersion, int Port, IReadOnlyList<IPAddress> Addresses, DateTime LastSeenUtc);

public sealed record LanDiscoveryOptions
{
    public required Guid DeviceId { get; init; }

    /// <summary>TCP port of the TLS listener, published in the SRV record.</summary>
    public required int Port { get; init; }

    /// <summary>Published as TXT <c>v</c>, e.g. "0.2".</summary>
    public required string ProtocolVersion { get; init; }

    /// <summary>How often to ask the network for PairSync instances.</summary>
    public TimeSpan QueryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An instance not heard from for this long counts as gone (three missed queries).</summary>
    public TimeSpan Expiry { get; init; } = TimeSpan.FromSeconds(95);

    /// <summary>Also use loopback interfaces and addresses; for tests with several instances on one machine.</summary>
    public bool IncludeLoopback { get; init; }
}

/// <summary>
/// Publishes this device as <c>_pairsync._tcp.local</c> and watches for others (plan §6). The TXT record carries only
/// the device id (<c>id</c>) and protocol version (<c>v</c>); the SRV record the port. Names are not published:
/// unknown devices learn nothing but that a PairSync instance is there.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public const string ServiceType = "_pairsync._tcp";

    private static readonly DomainName Service = new(ServiceType);

    private readonly LanDiscoveryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, LanServiceInfo> _seen = new();
    private readonly ConcurrentDictionary<string, (Guid Id, string Version)> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Host, int Port)> _srv = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IPAddress[]> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;
    private ITimer? _timer;

    public LanDiscovery(LanDiscoveryOptions options, TimeProvider time, ILogger<LanDiscovery> logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>A device appeared, or its port or addresses changed.</summary>
    public event Action<LanServiceInfo>? Seen;

    /// <summary>A device said goodbye or was not heard from within <see cref="LanDiscoveryOptions.Expiry"/>.</summary>
    public event Action<Guid>? Lost;

    public IReadOnlyCollection<LanServiceInfo> Current => [.. _seen.Values];

    /// <summary>Why mDNS could not start (no usable interface, UDP 5353 blocked); null when running.</summary>
    public string? Error { get; private set; }

    private string InstanceName => _options.DeviceId.ToString("N");

    public void Start()
    {
        if (_mdns is not null)
            return;
        try
        {
            if (_options.IncludeLoopback)
                MulticastService.IncludeLoopbackInterfaces = true; // process-wide switch of the library; tests only
            _mdns = new MulticastService(UsableInterfaces) { UseIpv6 = true, IgnoreDuplicateMessages = true };
            _discovery = new ServiceDiscovery(_mdns);
            _mdns.AnswerReceived += OnAnswer;
            _mdns.NetworkInterfaceDiscovered += (_, _) => Refresh();
            _discovery.ServiceInstanceShutdown += (_, e) => OnGoodbye(e.ServiceInstanceName);

            _profile = new ServiceProfile(new DomainName(InstanceName), Service, (ushort)_options.Port);
            var txt = _profile.Resources.OfType<TXTRecord>().Single();
            txt.Strings.Clear();
            txt.Strings.Add("id=" + _options.DeviceId.ToString("D"));
            txt.Strings.Add("v=" + _options.ProtocolVersion);

            _mdns.Start();
            _discovery.Advertise(_profile);
            Error = null;
        }
        catch (Exception e) when (e is SocketException or InvalidOperationException or NetworkInformationException)
        {
            Error = $"LAN discovery is not available: {e.Message}";
            _logger.LogWarning(e, "mDNS could not start; devices can still be paired by invitation");
            DisposeMdns();
            return;
        }

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _timer = _time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, _options.QueryInterval);
        _logger.LogInformation("Announcing {Service} on port {Port}", ServiceType, _options.Port);
    }

    /// <summary>Announces this device again and asks for others, e.g. after a network change.</summary>
    public void Refresh()
    {
        try
        {
            if (_profile is not null)
                _discovery?.Announce(_profile);
            _discovery?.QueryServiceInstances(Service);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(e, "mDNS refresh failed");
        }
    }

    private void Tick()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _options.Expiry;
        foreach (var info in _seen.Values.Where(i => i.LastSeenUtc < cutoff))
        {
            if (_seen.TryRemove(info.DeviceId, out _))
                Lost?.Invoke(info.DeviceId);
        }
        try
        {
            _discovery?.QueryServiceInstances(Service);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(e, "mDNS query failed");
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Refresh();

    private IEnumerable<NetworkInterface> UsableInterfaces(IEnumerable<NetworkInterface> interfaces) =>
        interfaces.Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast &&
                              (_options.IncludeLoopback || n.NetworkInterfaceType != NetworkInterfaceType.Loopback));

    private void OnAnswer(object? sender, MessageEventArgs e)
    {
        try
        {
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in e.Message.Answers.Concat(e.Message.AdditionalRecords))
                Remember(record, touched);
            foreach (var instance in touched)
                Update(instance, e.RemoteEndPoint?.Address);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException)
        {
            _logger.LogDebug(ex, "Ignored a malformed mDNS answer from {Remote}", e.RemoteEndPoint);
        }
    }

    /// <summary>Collects SRV, TXT and address records; records of one instance can arrive in separate messages.</summary>
    private void Remember(ResourceRecord record, HashSet<string> touched)
    {
        switch (record)
        {
            case SRVRecord srv when IsOurService(srv.Name):
                var name = srv.Name.ToString();
                if (srv.TTL == TimeSpan.Zero)
                {
                    OnGoodbye(srv.Name);
                    return;
                }
                _srv[name] = (srv.Target.ToString(), srv.Port);
                touched.Add(name);
                break;
            case TXTRecord txt when IsOurService(txt.Name) && ParseTxt(txt.Strings) is { } parsed:
                _txt[txt.Name.ToString()] = parsed;
                touched.Add(txt.Name.ToString());
                break;
            case AddressRecord address when Usable(address.Address):
                var host = address.Name.ToString();
                _hosts.AddOrUpdate(host, [address.Address],
                    (_, known) => known.Contains(address.Address) ? known : [.. known, address.Address]);
                foreach (var (instance, target) in _srv)
                {
                    if (string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase))
                        touched.Add(instance);
                }
                break;
        }
    }

    private void Update(string instance, IPAddress? sender)
    {
        if (!_txt.TryGetValue(instance, out var txt) || !_srv.TryGetValue(instance, out var srv))
            return;
        if (txt.Id == _options.DeviceId || !instance.StartsWith(txt.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
            return; // ourselves, or a TXT id that does not match the instance name

        // The sender address is known to be reachable; the advertised ones may include other networks.
        var addresses = new List<IPAddress>();
        if (sender is not null && Usable(sender))
            addresses.Add(sender.IsIPv4MappedToIPv6 ? sender.MapToIPv4() : sender);
        if (_hosts.TryGetValue(srv.Host, out var advertised))
            addresses.AddRange(advertised.Where(a => !addresses.Contains(a)));
        if (addresses.Count == 0)
            return;
        addresses.Sort((x, y) => (x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1) - (y.AddressFamily == AddressFamily.InterNetwork ? 0 : 1));

        var info = new LanServiceInfo(txt.Id, txt.Version, srv.Port, addresses, _time.GetUtcNow().UtcDateTime);
        var changed = true;
        _seen.AddOrUpdate(txt.Id, info, (_, old) =>
        {
            changed = old.Port != info.Port || old.ProtocolVersion != info.ProtocolVersion || !old.Addresses.SequenceEqual(info.Addresses);
            return info;
        });
        if (changed)
            Seen?.Invoke(info);
    }

    private void OnGoodbye(DomainName instanceName)
    {
        var name = instanceName.ToString();
        _srv.TryRemove(name, out _);
        if (_txt.TryRemove(name, out var txt) && _seen.TryRemove(txt.Id, out _))
            Lost?.Invoke(txt.Id);
    }

    private bool Usable(IPAddress address) =>
        (_options.IncludeLoopback || !IPAddress.IsLoopback(address)) &&
        !(address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || address.IsIPv6Multicast)) &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);

    private static bool IsOurService(DomainName name) => name.ToString().Contains("." + ServiceType + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>TXT <c>id=&lt;guid&gt;</c> and <c>v=&lt;major.minor&gt;</c>; anything else is ignored.</summary>
    public static (Guid Id, string Version)? ParseTxt(IEnumerable<string> strings)
    {
        Guid? id = null;
        string? version = null;
        foreach (var entry in strings)
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = entry[..separator];
            var value = entry[(separator + 1)..];
            if (key.Equals("id", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
                id = parsed;
            else if (key.Equals("v", StringComparison.OrdinalIgnoreCase) && value.Length is > 0 and <= 16)
                version = value;
        }
        return id is { } found && version is not null ? (found, version) : null;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _timer?.Dispose();
        DisposeMdns();
    }

    private void DisposeMdns()
    {
        try
        {
            if (_profile is not null)
                _discovery?.Unadvertise(_profile); // goodbye packets with TTL 0
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
        _discovery?.Dispose();
        _mdns?.Stop();
        _mdns?.Dispose();
        _discovery = null;
        _mdns = null;
    }
}
