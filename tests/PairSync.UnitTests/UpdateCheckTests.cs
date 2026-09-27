using System.Net;
using System.Text;
using PairSync.Application.Updates;

namespace PairSync.UnitTests;

public sealed class UpdateCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("v0.7.0", "0.7.0")]
    [InlineData("0.7", "0.7.0")]
    [InlineData("V1.2.3-beta.1", "1.2.3")]
    [InlineData("2", "2.0.0")]
    public void Release_tag_is_read(string tag, string expected)
    {
        Assert.True(UpdateCheck.TryParseTag(tag, out var version));
        Assert.Equal(expected, version.ToString(3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nightly")]
    public void Tag_without_version_is_rejected(string tag) => Assert.False(UpdateCheck.TryParseTag(tag, out _));

    [Theory]
    [InlineData("v0.7.0", UpdateState.NewerAvailable)]
    [InlineData("v0.6.0", UpdateState.UpToDate)]
    [InlineData("v0.5.9", UpdateState.UpToDate)]
    public async Task Latest_release_is_compared_with_the_running_version(string tag, UpdateState expected)
    {
        var handler = new FakeHandler(HttpStatusCode.OK, $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/RadiatorTwo/PairSync/releases/tag/{{tag}}"}""");

        var result = await new UpdateCheck(handler).CheckAsync(new Version(0, 6, 0, 0), Ct);

        Assert.Equal(expected, result.State);
        Assert.Equal($"https://github.com/RadiatorTwo/PairSync/releases/tag/{tag}", result.ReleaseUrl);
        Assert.Equal(UpdateCheck.LatestReleaseApi, handler.Requested);
        Assert.StartsWith("PairSync/", handler.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_release_yet_is_its_own_state()
    {
        var result = await new UpdateCheck(new FakeHandler(HttpStatusCode.NotFound, "{}")).CheckAsync(new Version(0, 6, 0), Ct);

        Assert.Equal(UpdateState.NoRelease, result.State);
    }

    [Fact]
    public async Task Network_error_is_reported_not_thrown()
    {
        var result = await new UpdateCheck(new FakeHandler(null, "")).CheckAsync(new Version(0, 6, 0), Ct);

        Assert.Equal(UpdateState.Failed, result.State);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Rate_limit_is_reported()
    {
        var result = await new UpdateCheck(new FakeHandler(HttpStatusCode.Forbidden, "{}")).CheckAsync(new Version(0, 6, 0), Ct);

        Assert.Equal(UpdateState.Failed, result.State);
        Assert.Contains("403", result.Error, StringComparison.Ordinal);
    }

    private sealed class FakeHandler(HttpStatusCode? status, string body) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        public string UserAgent { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            if (status is not { } code)
                throw new HttpRequestException("No route to host.");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
