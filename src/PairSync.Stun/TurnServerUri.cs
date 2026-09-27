using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PairSync.Stun;

public enum TurnTransport
{
    Udp,
    Tcp,
}

public enum TurnUriError
{
    None,
    Empty,

    /// <summary>Not <c>turn:</c> or <c>turns:</c>.</summary>
    UnsupportedScheme,
    InvalidHost,
    InvalidPort,

    /// <summary>A query other than <c>?transport=udp</c> or <c>?transport=tcp</c>, or user info in the URI.</summary>
    InvalidQuery,
}

/// <summary>
/// A TURN relay (RFC 7065 <c>turn:</c>/<c>turns:</c> URI) without credentials: <c>turn:host[:port][?transport=udp|tcp]</c>
/// or <c>turns:host[:port]</c> (TLS over TCP). Default ports 3478 and 5349. Credentials are kept apart and only joined
/// in <see cref="ToIceServer"/>, the form libdatachannel reads.
/// </summary>
public sealed record TurnServerUri
{
    public const int DefaultPort = 3478;
    public const int DefaultSecurePort = 5349;

    private const int MaxHostLength = 253;

    private TurnServerUri(bool secure, string host, int port, TurnTransport transport)
    {
        Secure = secure;
        Host = host;
        Port = port;
        Transport = transport;
    }

    /// <summary><c>turns:</c>: TLS over TCP.</summary>
    public bool Secure { get; }

    /// <summary>Host name in lower case, or an IP literal without brackets.</summary>
    public string Host { get; }

    public int Port { get; }

    /// <summary>Always <see cref="TurnTransport.Tcp"/> for <see cref="Secure"/>.</summary>
    public TurnTransport Transport { get; }

    /// <summary>Normalized form, e.g. <c>turn:relay.example.org:3478</c> or <c>turn:relay.example.org:3478?transport=tcp</c>.</summary>
    public override string ToString() => Format(null, null);

    /// <summary>
    /// The URI with credentials for the ICE configuration: <c>turn:user:pass@host:port?transport=…</c>, user and password
    /// percent-encoded (libdatachannel decodes them; TURN REST user names contain <c>:</c>).
    /// </summary>
    public string ToIceServer(string username, string password) => Format(username, password);

    private string Format(string? username, string? password)
    {
        var host = Host.Contains(':') ? $"[{Host}]" : Host;
        var credentials = username is null ? "" : $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password ?? "")}@";
        var query = Secure ? "" : Transport == TurnTransport.Tcp ? "?transport=tcp" : "";
        return $"{(Secure ? "turns" : "turn")}:{credentials}{host}:{Port}{query}";
    }

    /// <exception cref="FormatException">Not a usable TURN server URI.</exception>
    public static TurnServerUri Parse(string text) =>
        TryParse(text, out var uri, out var error) ? uri : throw new FormatException($"Not a TURN server URI ({error}): {text}");

    public static bool TryParse(string? text, [NotNullWhen(true)] out TurnServerUri? uri) => TryParse(text, out uri, out _);

    public static bool TryParse(string? text, [NotNullWhen(true)] out TurnServerUri? uri, out TurnUriError error)
    {
        uri = null;
        var rest = text.AsSpan().Trim();
        if (rest.IsEmpty)
            return Fail(TurnUriError.Empty, out error);

        bool secure;
        if (rest.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
        {
            secure = true;
            rest = rest["turns:".Length..];
        }
        else if (rest.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
        {
            secure = false;
            rest = rest["turn:".Length..];
        }
        else
            return Fail(TurnUriError.UnsupportedScheme, out error);
        if (rest.StartsWith("//", StringComparison.Ordinal))
            return Fail(TurnUriError.UnsupportedScheme, out error);
        if (rest.Contains('@'))
            return Fail(TurnUriError.InvalidQuery, out error);

        var transport = secure ? TurnTransport.Tcp : TurnTransport.Udp;
        var question = rest.IndexOf('?');
        if (question >= 0)
        {
            var query = rest[(question + 1)..];
            rest = rest[..question];
            if (query.Equals("transport=udp", StringComparison.OrdinalIgnoreCase) && !secure)
                transport = TurnTransport.Udp;
            else if (query.Equals("transport=tcp", StringComparison.OrdinalIgnoreCase))
                transport = TurnTransport.Tcp;
            else
                return Fail(TurnUriError.InvalidQuery, out error);
        }

        ReadOnlySpan<char> host, port;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0)
                return Fail(TurnUriError.InvalidHost, out error);
            host = rest[1..close];
            var after = rest[(close + 1)..];
            if (!after.IsEmpty && after[0] != ':')
                return Fail(TurnUriError.InvalidHost, out error);
            port = after.IsEmpty ? default : after[1..];
            if (!after.IsEmpty && port.IsEmpty)
                return Fail(TurnUriError.InvalidPort, out error);
            if (host.Contains('%') || !IPAddress.TryParse(host, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
                return Fail(TurnUriError.InvalidHost, out error);
            host = v6.ToString();
        }
        else
        {
            var colon = rest.IndexOf(':');
            if (colon >= 0 && rest[(colon + 1)..].Contains(':'))
                return Fail(TurnUriError.InvalidHost, out error);
            host = colon < 0 ? rest : rest[..colon];
            port = colon < 0 ? default : rest[(colon + 1)..];
            if (colon >= 0 && port.IsEmpty)
                return Fail(TurnUriError.InvalidPort, out error);
            if (host.IsEmpty || host.Length > MaxHostLength
                || Uri.CheckHostName(host.ToString()) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                return Fail(TurnUriError.InvalidHost, out error);
        }

        var portNumber = secure ? DefaultSecurePort : DefaultPort;
        if (!port.IsEmpty
            && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out portNumber) || portNumber is < 1 or > 65535))
            return Fail(TurnUriError.InvalidPort, out error);

        uri = new TurnServerUri(secure, host.ToString().ToLowerInvariant(), portNumber, transport);
        error = TurnUriError.None;
        return true;
    }

    private static bool Fail(TurnUriError reason, out TurnUriError error)
    {
        error = reason;
        return false;
    }
}
