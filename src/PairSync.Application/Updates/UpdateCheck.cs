using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PairSync.Application.Updates;

/// <summary>The running app's version, from <c>&lt;Version&gt;</c> in Directory.Build.props.</summary>
public static class AppInfo
{
    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>E.g. <c>0.6.0</c>.</summary>
    public static string DisplayVersion => Version.ToString(3);

    public const string ReleasesPage = "https://github.com/RadiatorTwo/PairSync/releases";

    internal static string ProductHeader => $"PairSync/{DisplayVersion}";
}

public enum UpdateState
{
    UpToDate,
    NewerAvailable,

    /// <summary>The project has published no release yet.</summary>
    NoRelease,
    Failed,
}

public sealed record UpdateResult(UpdateState State, string? LatestVersion = null, string? ReleaseUrl = null, string? Error = null);

/// <summary>
/// Looks up the latest release on GitHub (plan phase 6: update check without automatic installation). Runs only
/// when the user clicks "Check for updates"; it never downloads or installs anything.
/// </summary>
public sealed class UpdateCheck(HttpMessageHandler? handler = null)
{
    public static readonly Uri LatestReleaseApi = new("https://api.github.com/repos/RadiatorTwo/PairSync/releases/latest");

    public async Task<UpdateResult> CheckAsync(Version current, CancellationToken cancellationToken)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(AppInfo.ProductHeader));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateResult(UpdateState.NoRelease, ReleaseUrl: AppInfo.ReleasesPage);
            if (!response.IsSuccessStatusCode)
                return new UpdateResult(UpdateState.Failed, Error: $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            var release = await JsonSerializer.DeserializeAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), UpdateJsonContext.Default.Release, cancellationToken)
                .ConfigureAwait(false);
            if (release?.TagName is not { } tag || !TryParseTag(tag, out var latest))
                return new UpdateResult(UpdateState.Failed, Error: "The latest release has no version number.");
            var url = release.HtmlUrl ?? AppInfo.ReleasesPage;
            return latest > Normalize(current)
                ? new UpdateResult(UpdateState.NewerAvailable, latest.ToString(3), url)
                : new UpdateResult(UpdateState.UpToDate, latest.ToString(3), url);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new UpdateResult(UpdateState.Failed, Error: e is TaskCanceledException ? "GitHub did not answer in time." : e.Message);
        }
    }

    /// <summary>Reads <c>v1.2.3</c>, <c>1.2</c> or <c>v1.2.3-beta</c> (the suffix is ignored).</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
            text = text[..end];
        if (Version.TryParse(text, out var parsed) || (int.TryParse(text, out var major) && (parsed = new Version(major, 0)) is not null))
        {
            version = Normalize(parsed);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    internal sealed record Release(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}

[JsonSerializable(typeof(UpdateCheck.Release))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
