using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PairSync.Stun;

public enum StunUriError
{
    None,
    Empty,

    /// <summary><c>stuns:</c>, <c>turn:</c>, <c>turns:</c> or any <c>scheme://</c> form.</summary>
    UnsupportedScheme,
    InvalidHost,
    InvalidPort,
}

/// <summary>
/// A STUN server over UDP (RFC 7064 <c>stun:</c> URI). Accepted forms: <c>stun:host:port</c>, <c>stun:host</c>
/// (port 3478), bare <c>host:port</c> or <c>host</c>, and IPv6 literals in brackets (<c>stun:[2001:db8::1]:3478</c>).
/// <c>stuns:</c> (STUN over TLS) and <c>turn:</c>/<c>turns:</c> are rejected with
/// <see cref="StunUriError.UnsupportedScheme"/>: the client speaks plain UDP only and relays come later, so the NAT
/// diagnosis reports such entries as invalid instead of querying them.
/// </summary>
public sealed record StunServerUri
{
    public const int DefaultPort = 3478;

    private const int MaxHostLength = 253;

    private static readonly string[] UnsupportedSchemes = ["stuns:", "turn:", "turns:"];

    private StunServerUri(string host, int port)
    {
        Host = host;
        Port = port;
    }

    /// <summary>Host name in lower case, or an IP literal without brackets.</summary>
    public string Host { get; }

    public int Port { get; }

    /// <summary>The literal address when <see cref="Host"/> is an IP address, otherwise <c>null</c>.</summary>
    public IPAddress? Address => IPAddress.TryParse(Host, out var address) ? address : null;

    /// <summary>Normalized form, e.g. <c>stun:stun.example.org:3478</c> or <c>stun:[2001:db8::1]:3478</c>.</summary>
    public override string ToString() => Host.Contains(':') ? $"stun:[{Host}]:{Port}" : $"stun:{Host}:{Port}";

    /// <exception cref="FormatException">Not a usable STUN server URI.</exception>
    public static StunServerUri Parse(string text) =>
        TryParse(text, out var uri, out var error) ? uri : throw new FormatException($"Not a STUN server URI ({error}): {text}");

    public static bool TryParse(string? text, [NotNullWhen(true)] out StunServerUri? uri) => TryParse(text, out uri, out _);

    public static bool TryParse(string? text, [NotNullWhen(true)] out StunServerUri? uri, out StunUriError error)
    {
        uri = null;
        var rest = text.AsSpan().Trim();
        if (rest.IsEmpty)
            return Fail(StunUriError.Empty, out error);
        if (rest.Contains("://", StringComparison.Ordinal))
            return Fail(StunUriError.UnsupportedScheme, out error);
        foreach (var scheme in UnsupportedSchemes)
        {
            if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return Fail(StunUriError.UnsupportedScheme, out error);
        }
        if (rest.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
            rest = rest["stun:".Length..];

        ReadOnlySpan<char> host, port;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0)
                return Fail(StunUriError.InvalidHost, out error);
            host = rest[1..close];
            var after = rest[(close + 1)..];
            if (!after.IsEmpty && after[0] != ':')
                return Fail(StunUriError.InvalidHost, out error);
            port = after.IsEmpty ? default : after[1..];
            if (!after.IsEmpty && port.IsEmpty)
                return Fail(StunUriError.InvalidPort, out error);
            if (host.Contains('%') || !IPAddress.TryParse(host, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
                return Fail(StunUriError.InvalidHost, out error);
            host = v6.ToString();
        }
        else
        {
            var colon = rest.IndexOf(':');
            if (colon >= 0 && rest[(colon + 1)..].Contains(':'))
                return Fail(StunUriError.InvalidHost, out error); // IPv6 literal without brackets
            host = colon < 0 ? rest : rest[..colon];
            port = colon < 0 ? default : rest[(colon + 1)..];
            if (colon >= 0 && port.IsEmpty)
                return Fail(StunUriError.InvalidPort, out error);
            if (host.IsEmpty || host.Length > MaxHostLength
                || Uri.CheckHostName(host.ToString()) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                return Fail(StunUriError.InvalidHost, out error);
        }

        var portNumber = DefaultPort;
        if (!port.IsEmpty
            && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out portNumber) || portNumber is < 1 or > 65535))
            return Fail(StunUriError.InvalidPort, out error);

        uri = new StunServerUri(host.ToString().ToLowerInvariant(), portNumber);
        error = StunUriError.None;
        return true;
    }

    private static bool Fail(StunUriError reason, out StunUriError error)
    {
        error = reason;
        return false;
    }
}
