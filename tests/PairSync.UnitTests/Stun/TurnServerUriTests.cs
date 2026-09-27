using PairSync.Stun;

namespace PairSync.UnitTests.Stun;

public sealed class TurnServerUriTests
{
    [Theory]
    [InlineData("turn:relay.example.org", "relay.example.org", 3478, false, TurnTransport.Udp, "turn:relay.example.org:3478")]
    [InlineData(" TURN:Relay.Example.org:3479 ", "relay.example.org", 3479, false, TurnTransport.Udp, "turn:relay.example.org:3479")]
    [InlineData("turn:relay.example.org?transport=udp", "relay.example.org", 3478, false, TurnTransport.Udp, "turn:relay.example.org:3478")]
    [InlineData("turn:relay.example.org:80?transport=TCP", "relay.example.org", 80, false, TurnTransport.Tcp, "turn:relay.example.org:80?transport=tcp")]
    [InlineData("turns:relay.example.org", "relay.example.org", 5349, true, TurnTransport.Tcp, "turns:relay.example.org:5349")]
    [InlineData("turns:relay.example.org:443?transport=tcp", "relay.example.org", 443, true, TurnTransport.Tcp, "turns:relay.example.org:443")]
    [InlineData("turn:[2001:DB8::5]:3478", "2001:db8::5", 3478, false, TurnTransport.Udp, "turn:[2001:db8::5]:3478")]
    [InlineData("turn:192.0.2.9", "192.0.2.9", 3478, false, TurnTransport.Udp, "turn:192.0.2.9:3478")]
    public void Valid_uri_is_parsed_and_normalized(string text, string host, int port, bool secure, TurnTransport transport, string normalized)
    {
        Assert.True(TurnServerUri.TryParse(text, out var uri, out var error));

        Assert.Equal(TurnUriError.None, error);
        Assert.Equal(host, uri.Host);
        Assert.Equal(port, uri.Port);
        Assert.Equal(secure, uri.Secure);
        Assert.Equal(transport, uri.Transport);
        Assert.Equal(normalized, uri.ToString());
        Assert.Equal(uri, TurnServerUri.Parse(uri.ToString()));
    }

    [Theory]
    [InlineData(null, TurnUriError.Empty)]
    [InlineData("  ", TurnUriError.Empty)]
    [InlineData("stun:relay.example.org", TurnUriError.UnsupportedScheme)]
    [InlineData("relay.example.org:3478", TurnUriError.UnsupportedScheme)]
    [InlineData("turn://relay.example.org", TurnUriError.UnsupportedScheme)]
    [InlineData("turn:user:pass@relay.example.org", TurnUriError.InvalidQuery)]
    [InlineData("turn:relay.example.org?transport=tls", TurnUriError.InvalidQuery)]
    [InlineData("turns:relay.example.org?transport=udp", TurnUriError.InvalidQuery)]
    [InlineData("turn:", TurnUriError.InvalidHost)]
    [InlineData("turn:2001:db8::1", TurnUriError.InvalidHost)]
    [InlineData("turn:relay.example.org:", TurnUriError.InvalidPort)]
    [InlineData("turn:relay.example.org:70000", TurnUriError.InvalidPort)]
    public void Invalid_uri_is_rejected_with_reason(string? text, TurnUriError expected)
    {
        Assert.False(TurnServerUri.TryParse(text, out var uri, out var error));

        Assert.Null(uri);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Ice_server_carries_percent_encoded_credentials()
    {
        var uri = TurnServerUri.Parse("turn:relay.example.org:3478?transport=tcp");

        Assert.Equal("turn:1790000000%3Aab12:p%40ss%2Fw%3Ard@relay.example.org:3478?transport=tcp",
            uri.ToIceServer("1790000000:ab12", "p@ss/w:rd"));
    }
}
