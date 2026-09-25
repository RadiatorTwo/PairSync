using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PairSync.Transport.Tls;

/// <summary>
/// Where a listening device can be reached and the SHA-256 of its public key (SubjectPublicKeyInfo), pinned by the
/// connecting side. The hash equals the device fingerprint. In the product this comes from mDNS plus the paired
/// device key; the spike passes it as a compact code.
/// </summary>
public sealed record TlsEndpointInfo(IReadOnlyList<IPAddress> Addresses, int Port, string PublicKeySha256)
{
    public const string CodePrefix = "PST2:";

    public string ToCode()
    {
        var text = $"{Port}|{PublicKeySha256}|{string.Join(",", Addresses)}";
        return CodePrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static TlsEndpointInfo FromCode(string code)
    {
        code = code.Trim();
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal))
            throw new FormatException($"Expected a code starting with {CodePrefix}");
        var base64 = code[CodePrefix.Length..].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var port) || port is <= 0 or > 65535 || parts[1].Length != 64)
            throw new FormatException("Malformed connection code.");
        var addresses = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(IPAddress.Parse).ToArray();
        return new TlsEndpointInfo(addresses, port, parts[1].ToLowerInvariant());
    }

    /// <summary>Lower-case hex SHA-256 of the certificate's SubjectPublicKeyInfo.</summary>
    public static string KeyHash(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>The SubjectPublicKeyInfo of a certificate presented in a TLS handshake.</summary>
    public static byte[]? PublicKeyOf(X509Certificate? certificate)
    {
        if (certificate is null)
            return null;
        using var parsed = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return parsed.PublicKey.ExportSubjectPublicKeyInfo();
    }

    /// <summary>Usable unicast addresses of this machine, IPv4 first; loopback and IPv6 link-local are skipped.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork ||
                        (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Distinct()
            .ToArray();
}
