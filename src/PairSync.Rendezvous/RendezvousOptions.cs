using System.Security.Cryptography;
using System.Text;
using PairSync.Protocol.Rendezvous;

namespace PairSync.Rendezvous;

/// <summary>Settings of the rendezvous service, section <c>Rendezvous</c> in <c>appsettings.json</c> or <c>Rendezvous__…</c> variables.</summary>
public sealed class RendezvousOptions
{
    /// <summary>A device must log in within this time after connecting.</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>WebSocket keep-alive ping interval.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxConnectionsPerIp { get; set; } = 16;

    /// <summary>Messages (watch and send) per connection and minute; more closes nothing but answers <c>rate-limited</c>.</summary>
    public int MessagesPerMinute { get; set; } = 60;

    /// <summary>All devices at once; protects a small server.</summary>
    public int MaxConnections { get; set; } = 10_000;

    public TurnOptions Turn { get; set; } = new();
}

/// <summary>
/// TURN relays whose short-lived credentials the service hands to logged-in devices, following the coturn REST
/// scheme (<c>use-auth-secret</c> with the same <see cref="Secret"/> in <c>turnserver.conf</c>).
/// </summary>
public sealed class TurnOptions
{
    /// <summary>E.g. <c>turn:relay.example.org:3478</c>, <c>turns:relay.example.org:5349</c>. Empty: no relays.</summary>
    public List<string> Uris { get; set; } = [];

    public string? Secret { get; set; }

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(12);

    public bool IsEnabled => Uris.Count > 0 && !string.IsNullOrEmpty(Secret);

    /// <summary>Credentials for <paramref name="address"/>: user <c>&lt;unix expiry&gt;:&lt;address&gt;</c>, password <c>base64(HMAC-SHA1(secret, user))</c>.</summary>
    public (IReadOnlyList<RvIceServer> Servers, DateTime? ExpiresAtUtc) CredentialsFor(string address, DateTimeOffset now)
    {
        if (!IsEnabled)
            return ([], null);
        var expires = now + Lifetime;
        var username = $"{expires.ToUnixTimeSeconds()}:{address}";
        var password = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Secret!), Encoding.UTF8.GetBytes(username)));
        return ([.. Uris.Select(u => new RvIceServer(u.Trim(), username, password))], expires.UtcDateTime);
    }
}
