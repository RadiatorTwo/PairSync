using PairSync.Stun;

namespace PairSync.UnitTests.Stun;

public sealed class StunServerUriTests
{
    [Theory]
    [InlineData("stun:stun.cloudflare.com:3478", "stun.cloudflare.com", 3478, "stun:stun.cloudflare.com:3478")]
    [InlineData("stun:stun.l.google.com:19302", "stun.l.google.com", 19302, "stun:stun.l.google.com:19302")]
    [InlineData("  STUN:Stun.Example.ORG  ", "stun.example.org", 3478, "stun:stun.example.org:3478")]
    [InlineData("stun.example.org:5349", "stun.example.org", 5349, "stun:stun.example.org:5349")]
    [InlineData("stun.example.org", "stun.example.org", 3478, "stun:stun.example.org:3478")]
    [InlineData("localhost:3478", "localhost", 3478, "stun:localhost:3478")]
    [InlineData("stun:192.0.2.10:3479", "192.0.2.10", 3479, "stun:192.0.2.10:3479")]
    [InlineData("stun:[2001:DB8::1]:3478", "2001:db8::1", 3478, "stun:[2001:db8::1]:3478")]
    [InlineData("[2001:db8::1]", "2001:db8::1", 3478, "stun:[2001:db8::1]:3478")]
    [InlineData("stun:[::1]:1", "::1", 1, "stun:[::1]:1")]
    public void Valid_uri_is_parsed_and_normalized(string text, string host, int port, string normalized)
    {
        Assert.True(StunServerUri.TryParse(text, out var uri, out var error));

        Assert.Equal(StunUriError.None, error);
        Assert.Equal(host, uri.Host);
        Assert.Equal(port, uri.Port);
        Assert.Equal(normalized, uri.ToString());
        Assert.Equal(uri, StunServerUri.Parse(uri.ToString()));
    }

    [Theory]
    [InlineData(null, StunUriError.Empty)]
    [InlineData("   ", StunUriError.Empty)]
    [InlineData("turn:turn.example.org:3478", StunUriError.UnsupportedScheme)]
    [InlineData("turns:turn.example.org:5349", StunUriError.UnsupportedScheme)]
    [InlineData("stuns:stun.example.org:5349", StunUriError.UnsupportedScheme)]
    [InlineData("http://stun.example.org", StunUriError.UnsupportedScheme)]
    [InlineData("stun:", StunUriError.InvalidHost)]
    [InlineData("stun::3478", StunUriError.InvalidHost)]
    [InlineData("stun:2001:db8::1", StunUriError.InvalidHost)]
    [InlineData("stun:[2001:db8::1", StunUriError.InvalidHost)]
    [InlineData("stun:[192.0.2.1]:3478", StunUriError.InvalidHost)]
    [InlineData("stun:[fe80::1%eth0]:3478", StunUriError.InvalidHost)]
    [InlineData("stun:[2001:db8::1]x", StunUriError.InvalidHost)]
    [InlineData("stun:host name:3478", StunUriError.InvalidHost)]
    [InlineData("stun:user@host:3478", StunUriError.InvalidHost)]
    [InlineData("stun:host/path", StunUriError.InvalidHost)]
    [InlineData("stun:host:", StunUriError.InvalidPort)]
    [InlineData("stun:host:0", StunUriError.InvalidPort)]
    [InlineData("stun:host:65536", StunUriError.InvalidPort)]
    [InlineData("stun:host:+80", StunUriError.InvalidPort)]
    [InlineData("stun:host:80?transport=udp", StunUriError.InvalidPort)]
    [InlineData("stun:[2001:db8::1]:", StunUriError.InvalidPort)]
    public void Invalid_uri_is_rejected_with_reason(string? text, StunUriError expected)
    {
        Assert.False(StunServerUri.TryParse(text, out var uri, out var error));

        Assert.Null(uri);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Parse_throws_format_exception() =>
        Assert.Throws<FormatException>(() => StunServerUri.Parse("turn:relay.example.org"));
}
