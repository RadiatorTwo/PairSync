using System.Net;
using System.Net.Sockets;

namespace PairSync.Stun;

/// <summary>Address ranges the NAT diagnosis cares about. IPv4-mapped IPv6 addresses are treated as IPv4.</summary>
public static class IpAddressRanges
{
    /// <summary>Carrier-grade NAT shared address space, 100.64.0.0/10 (RFC 6598).</summary>
    public static bool IsCgnat(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    /// <summary>RFC 1918, IPv4 link-local, IPv6 unique local (fc00::/7), link-local and site-local.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            return b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
            return true;
        return false;
    }

    /// <summary>
    /// An answer a DNS filter (Pi-hole, AdGuard, router block lists) gives instead of the real server:
    /// unspecified, loopback, private or CGNAT addresses. A public STUN server never has one of these.
    /// </summary>
    public static bool IsDnsFilterAnswer(IPAddress address)
    {
        address = Normalize(address);
        return address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(address)
            || IsPrivate(address) || IsCgnat(address);
    }

    /// <summary>Global unicast IPv6 (2000::/3), excluding Teredo, which is a tunnel and not native IPv6.</summary>
    public static bool IsGlobalIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6 || address.IsIPv6Teredo)
            return false;
        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out _);
        return (b[0] & 0xE0) == 0x20;
    }

    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
