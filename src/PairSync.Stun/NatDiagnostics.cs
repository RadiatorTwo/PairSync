using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PairSync.Stun;

/// <summary>Resolves a host name to its addresses; throws <see cref="SocketException"/> when the name does not exist.</summary>
public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

public sealed record NatDiagnosticsOptions
{
    public static NatDiagnosticsOptions Default { get; } = new();

    /// <summary>Retransmission and timeout of each Binding transaction in the full diagnosis.</summary>
    public StunClientOptions Stun { get; init; } = StunClientOptions.Default;

    /// <summary>Per-name limit for DNS resolution.</summary>
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Transaction timeout and DNS limit of <see cref="NatDiagnostics.QuickHintAsync"/>.</summary>
    public TimeSpan QuickTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Whether the full diagnosis also tries STUN over IPv6 (only when a global IPv6 address exists).</summary>
    public bool CheckIpv6 { get; init; } = true;

    public HostResolver ResolveHostAsync { get; init; } = (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    /// <summary>Unicast addresses of the interfaces that are up, loopback excluded.</summary>
    public Func<IReadOnlyList<IPAddress>> GetLocalAddresses { get; init; } = InterfaceAddresses;

    private static IReadOnlyList<IPAddress> InterfaceAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}

/// <summary>
/// Finds out why a direct internet connection may fail (plan §12): DNS filters, UDP blocked, symmetric mapping,
/// CGNAT and IPv6. Only the given STUN servers are contacted, nothing else. IPv4 requests to all servers go out
/// concurrently from one local socket, so their mapped addresses can be compared.
/// </summary>
public sealed class NatDiagnostics(NatDiagnosticsOptions? options = null)
{
    private readonly NatDiagnosticsOptions _options = options ?? NatDiagnosticsOptions.Default;

    /// <summary>Full diagnosis; with default options it takes at most DNS timeout + STUN timeout (about 7 s).</summary>
    public Task<NatReport> RunAsync(IEnumerable<string> servers, CancellationToken cancellationToken) =>
        RunCoreAsync(servers, _options.Stun, _options.DnsTimeout, _options.CheckIpv6, cancellationToken);

    /// <summary>
    /// Short IPv4-only run for embedding a <see cref="NatHint"/> in a connection code: STUN transactions and DNS are
    /// each limited to <see cref="NatDiagnosticsOptions.QuickTimeout"/>.
    /// </summary>
    public async Task<NatHint> QuickHintAsync(IEnumerable<string> servers, CancellationToken cancellationToken)
    {
        var stun = _options.Stun with { Timeout = _options.QuickTimeout };
        var dnsTimeout = _options.DnsTimeout < _options.QuickTimeout ? _options.DnsTimeout : _options.QuickTimeout;
        var report = await RunCoreAsync(servers, stun, dnsTimeout, checkIpv6: false, cancellationToken).ConfigureAwait(false);
        return report.Hint;
    }

    private async Task<NatReport> RunCoreAsync(
        IEnumerable<string> servers, StunClientOptions stun, TimeSpan dnsTimeout, bool checkIpv6, CancellationToken cancellationToken)
    {
        var time = stun.TimeProvider;
        var startedAt = time.GetUtcNow();
        var started = time.GetTimestamp();

        var localAddresses = _options.GetLocalAddresses().Select(IpAddressRanges.Normalize).ToList();
        var hasGlobalIpv6 = localAddresses.Any(IpAddressRanges.IsGlobalIpv6);

        var targets = await Task.WhenAll(servers.Select(s => ResolveAsync(s, dnsTimeout, cancellationToken))).ConfigureAwait(false);

        using var ipv4 = TryCreate(AddressFamily.InterNetwork, stun);
        using var ipv6 = checkIpv6 && hasGlobalIpv6 ? TryCreate(AddressFamily.InterNetworkV6, stun) : null;
        var checks = await Task.WhenAll(targets.Select(t => QueryAsync(t, ipv4, ipv6, cancellationToken))).ConfigureAwait(false);

        var ipv4Answers = checks.Select(c => c.Ipv4).OfType<StunBindingResult>().Where(r => r.IsSuccess)
            .DistinctBy(r => r.Server).ToList();
        var mapping = ClassifyMapping(ipv4Answers, ipv4?.LocalEndPoint?.Port, localAddresses);
        var attempted = checks.Any(c => c.Ipv4 is not null || c.Ipv6 is not null);
        var anyResponse = checks.Any(c => c.Ipv4?.Answered == true || c.Ipv6?.Answered == true);
        var udpBlocked = attempted && !anyResponse;
        var publicEndPoint = ipv4Answers.FirstOrDefault()?.MappedAddress;
        var ipv6Answer = checks.Select(c => c.Ipv6).OfType<StunBindingResult>().FirstOrDefault(r => r.IsSuccess);

        return new NatReport
        {
            Servers = checks,
            Mapping = mapping,
            UdpBlocked = udpBlocked,
            DnsFilterSuspected = checks.Any(c => c.Status == StunServerStatus.DnsBlocked)
                || (targets.Any(t => t.NameNotFound) && targets.Any(t => t.Status is null && t.Uri?.Address is null)),
            CgnatSuspected = ipv4Answers.Any(r => IpAddressRanges.IsCgnat(r.MappedAddress!.Address))
                || localAddresses.Any(IpAddressRanges.IsCgnat),
            PublicEndPoint = publicEndPoint,
            HasGlobalIpv6Address = hasGlobalIpv6,
            Ipv6Checked = ipv6 is not null,
            Ipv6StunReachable = ipv6Answer is not null,
            PublicIpv6EndPoint = ipv6Answer?.MappedAddress,
            Hint = udpBlocked
                ? NatHint.UdpBlocked
                : mapping switch
                {
                    NatMappingBehavior.NoNat => NatHint.Open,
                    NatMappingBehavior.EndpointIndependent => NatHint.EndpointIndependent,
                    NatMappingBehavior.EndpointDependent => NatHint.Symmetric,
                    _ => NatHint.Unknown,
                },
            StartedAt = startedAt,
            Duration = time.GetElapsedTime(started),
        };
    }

    /// <summary>
    /// Same mapping from every answering server → endpoint-independent; any difference → endpoint-dependent.
    /// Servers count as distinct per address and port, so two ports of one host also reveal port-dependent mapping.
    /// </summary>
    private static NatMappingBehavior ClassifyMapping(List<StunBindingResult> answers, int? localPort, List<IPAddress> localAddresses)
    {
        if (answers.Count == 0)
            return NatMappingBehavior.Unknown;
        var distinct = answers.Select(a => a.MappedAddress!).Distinct().Count();
        if (distinct > 1)
            return NatMappingBehavior.EndpointDependent;
        var mapped = answers[0].MappedAddress!;
        if (mapped.Port == localPort && localAddresses.Contains(mapped.Address))
            return NatMappingBehavior.NoNat;
        return answers.Count >= 2 ? NatMappingBehavior.EndpointIndependent : NatMappingBehavior.Unknown;
    }

    private async Task<Target> ResolveAsync(string server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!StunServerUri.TryParse(server, out var uri, out var error))
            return new Target(server, null, [], [], StunServerStatus.InvalidUri, error.ToString());
        if (uri.Address is { } literal)
            return new Target(server, uri, [literal], [IpAddressRanges.Normalize(literal)], null, null);

        IPAddress[] addresses;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            addresses = await _options.ResolveHostAsync(uri.Host, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Target(server, uri, [], [], StunServerStatus.DnsFailed, "DNS timeout");
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            return new Target(server, uri, [], [], StunServerStatus.DnsFailed, "name not found", NameNotFound: true);
        }
        catch (SocketException e)
        {
            return new Target(server, uri, [], [], StunServerStatus.DnsFailed, e.SocketErrorCode.ToString());
        }
        if (addresses.Length == 0)
            return new Target(server, uri, [], [], StunServerStatus.DnsFailed, "name not found", NameNotFound: true);

        var usable = addresses.Select(IpAddressRanges.Normalize).Where(a => !IpAddressRanges.IsDnsFilterAnswer(a)).ToList();
        if (usable.Count == 0)
            return new Target(server, uri, addresses, [], StunServerStatus.DnsBlocked,
                "resolved to " + string.Join(", ", addresses.Select(a => a.ToString())));
        return new Target(server, uri, addresses, usable, null, null);
    }

    private static async Task<StunServerCheck> QueryAsync(Target target, StunClient? ipv4, StunClient? ipv6, CancellationToken cancellationToken)
    {
        if (target.Status is { } status)
            return new StunServerCheck
            {
                Server = target.Server, Uri = target.Uri, Status = status, ResolvedAddresses = target.Resolved, Detail = target.Detail,
            };

        var uri = target.Uri!;
        var v4Address = target.Usable.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var v6Address = target.Usable.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        var v4Task = BindingAsync(ipv4, v4Address, uri.Port, cancellationToken);
        var v6Task = BindingAsync(ipv6, v6Address, uri.Port, cancellationToken);
        StunBindingResult? v4 = await v4Task.ConfigureAwait(false), v6 = await v6Task.ConfigureAwait(false);

        StunServerStatus result;
        string? detail = null;
        if (v4?.IsSuccess == true || v6?.IsSuccess == true)
            result = StunServerStatus.Answered;
        else if (v4?.Answered == true || v6?.Answered == true)
        {
            result = StunServerStatus.Failed;
            var failed = v4?.Answered == true ? v4 : v6!;
            detail = failed.ErrorCode is { } code ? $"error {code} {failed.Detail}".TrimEnd() : failed.Detail;
        }
        else if (v4 is not null || v6 is not null)
        {
            result = StunServerStatus.NoResponse;
            detail = (v4 ?? v6)!.Status == StunBindingStatus.NetworkError ? (v4 ?? v6)!.Detail : "no response";
        }
        else
        {
            result = StunServerStatus.NoUsableAddress;
            detail = "no address of a usable family";
        }

        return new StunServerCheck
        {
            Server = target.Server, Uri = uri, Status = result, ResolvedAddresses = target.Resolved, Ipv4 = v4, Ipv6 = v6, Detail = detail,
        };
    }

    private static async Task<StunBindingResult?> BindingAsync(
        StunClient? client, IPAddress? address, int port, CancellationToken cancellationToken) =>
        client is null || address is null
            ? null
            : await client.BindingAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);

    private static StunClient? TryCreate(AddressFamily family, StunClientOptions options)
    {
        try
        {
            return StunClient.Create(family, options);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private sealed record Target(
        string Server, StunServerUri? Uri, IReadOnlyList<IPAddress> Resolved, IReadOnlyList<IPAddress> Usable,
        StunServerStatus? Status, string? Detail, bool NameNotFound = false);
}
