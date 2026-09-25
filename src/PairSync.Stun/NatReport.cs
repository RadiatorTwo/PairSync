using System.Net;

namespace PairSync.Stun;

public enum StunServerStatus
{
    /// <summary>At least one Binding request (IPv4 or IPv6) got a success response.</summary>
    Answered,

    /// <summary>The server responded, but with an error or an unusable response.</summary>
    Failed,

    /// <summary>Requests were sent, nothing came back.</summary>
    NoResponse,

    /// <summary>The entry is not a <c>stun:</c> URI this client can query, see <see cref="StunServerUri"/>.</summary>
    InvalidUri,

    /// <summary>The name did not resolve (no such host, no DNS, timeout).</summary>
    DnsFailed,

    /// <summary>Every answer was 0.0.0.0, ::, loopback, private or CGNAT: a DNS filter blocks the server.</summary>
    DnsBlocked,

    /// <summary>Resolved, but to no address of a family this machine can use.</summary>
    NoUsableAddress,
}

/// <summary>Result of the checks against one configured server.</summary>
public sealed record StunServerCheck
{
    /// <summary>The entry as configured.</summary>
    public required string Server { get; init; }

    public StunServerUri? Uri { get; init; }

    public required StunServerStatus Status { get; init; }

    /// <summary>All addresses DNS returned (or the literal), including filtered ones.</summary>
    public IReadOnlyList<IPAddress> ResolvedAddresses { get; init; } = [];

    public StunBindingResult? Ipv4 { get; init; }

    public StunBindingResult? Ipv6 { get; init; }

    /// <summary>Why the check failed, for the report; <c>null</c> when it succeeded.</summary>
    public string? Detail { get; init; }
}

/// <summary>Outcome of <see cref="NatDiagnostics.RunAsync"/>.</summary>
public sealed record NatReport
{
    public required IReadOnlyList<StunServerCheck> Servers { get; init; }

    /// <summary>Compared over IPv4 from one local socket.</summary>
    public required NatMappingBehavior Mapping { get; init; }

    /// <summary>Requests went out but no server responded at all.</summary>
    public required bool UdpBlocked { get; init; }

    /// <summary>
    /// At least one server name was answered with a filter address, or one name was "not found" while another
    /// resolved normally (filters that answer NXDOMAIN/NODATA, seen with <c>stun.l.google.com</c> in Phase 0).
    /// </summary>
    public required bool DnsFilterSuspected { get; init; }

    /// <summary>
    /// The public or a local IPv4 address lies in 100.64.0.0/10. Only a hint: CGNAT with a private shared range
    /// looks like any other symmetric NAT.
    /// </summary>
    public required bool CgnatSuspected { get; init; }

    /// <summary>The IPv4 mapping the first answering server saw.</summary>
    public IPEndPoint? PublicEndPoint { get; init; }

    public required bool HasGlobalIpv6Address { get; init; }

    /// <summary>Whether STUN over IPv6 was attempted (needs a global address and full mode).</summary>
    public required bool Ipv6Checked { get; init; }

    public required bool Ipv6StunReachable { get; init; }

    public IPEndPoint? PublicIpv6EndPoint { get; init; }

    public required NatHint Hint { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required TimeSpan Duration { get; init; }
}
