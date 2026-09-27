using System.Text.Json.Serialization;

namespace PairSync.Storage.Settings;

/// <summary>What closing the main window does (plan §10: closing and quitting are separate).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CloseBehavior>))]
public enum CloseBehavior
{
    /// <summary>Hide to the tray; falls back to <see cref="Minimize"/> without a tray host.</summary>
    Tray = 0,
    Minimize = 1,
    Quit = 2,
}

/// <summary>User settings stored in <c>settings.json</c>. Unknown or missing values fall back to the defaults.</summary>
public sealed record AppSettings
{
    public const int DefaultPort = 47800;

    /// <summary>Shown to other devices; null means the computer name.</summary>
    public string? DeviceName { get; init; }

    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.Tray;

    public bool StartWithSystem { get; init; }

    /// <summary>TCP port for incoming LAN connections.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Upload limit in bytes per second; 0 = unlimited.</summary>
    public long UploadLimitBytesPerSecond { get; init; }

    public int ParallelTransfers { get; init; } = 2;

    /// <summary>Debug-level diagnostic logs (never file contents or keys).</summary>
    public bool VerboseLogging { get; init; }

    /// <summary>
    /// STUN servers for internet connections (<c>stun:host:port</c>). Queried only when the user starts an internet
    /// connection or the NAT diagnostic, never for the LAN. Empty turns internet connections off.
    /// </summary>
    public IReadOnlyList<string> StunServers { get; init; } = DefaultStunServers;

    public static IReadOnlyList<string> DefaultStunServers { get; } = ["stun:stun.cloudflare.com:3478", "stun:stun.l.google.com:19302"];

    public const int MaxStunServers = 8;

    /// <summary>
    /// Optional self-hosted rendezvous service (<c>wss://host/v1</c>) for presence and automatic internet connections.
    /// Null: off, no request to any service.
    /// </summary>
    public string? RendezvousUrl { get; init; }

    /// <summary>
    /// <c>NAME=value</c> lines for paths in Claude Code commands from another device, e.g. <c>TOOLS_ROOT=D:	ools</c>.
    /// The other device writes <c>${TOOLS_ROOT}</c>; this device puts in its value.
    /// </summary>
    public IReadOnlyList<string> PathVariables { get; init; } = [];

    public const int MaxPathVariables = 32;

    [JsonIgnore]
    public string EffectiveDeviceName => string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();

    /// <summary>Clamps values a hand-edited file might get wrong.</summary>
    public AppSettings Normalized()
    {
        var name = DeviceName?.Trim();
        return this with
        {
            DeviceName = string.IsNullOrEmpty(name) ? null : name[..Math.Min(name.Length, MaxDeviceNameLength)],
            CloseBehavior = Enum.IsDefined(CloseBehavior) ? CloseBehavior : CloseBehavior.Tray,
            Port = Port is >= 1024 and <= 65535 ? Port : DefaultPort,
            UploadLimitBytesPerSecond = Math.Max(0, UploadLimitBytesPerSecond),
            ParallelTransfers = Math.Clamp(ParallelTransfers, 1, 8),
            StunServers = StunServers is null
                ? DefaultStunServers
                : [.. StunServers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxStunServers)],
            RendezvousUrl = RendezvousUrls.TryNormalize(RendezvousUrl, out var url) ? url : null,
            PathVariables = PathVariables is null
                ? []
                : [.. PathVariables.Where(v => v?.Contains('=') == true).Select(v => v.Trim()).Take(MaxPathVariables)],
        };
    }

    public const int MaxDeviceNameLength = 64;

    // Value equality including the list (the generated one compares it by reference). New properties go here too.
    public bool Equals(AppSettings? other) =>
        other is not null
        && DeviceName == other.DeviceName
        && CloseBehavior == other.CloseBehavior
        && StartWithSystem == other.StartWithSystem
        && Port == other.Port
        && UploadLimitBytesPerSecond == other.UploadLimitBytesPerSecond
        && ParallelTransfers == other.ParallelTransfers
        && VerboseLogging == other.VerboseLogging
        && StunServers.SequenceEqual(other.StunServers)
        && RendezvousUrl == other.RendezvousUrl
        && PathVariables.SequenceEqual(other.PathVariables);

    public override int GetHashCode() =>
        HashCode.Combine(DeviceName, CloseBehavior, StartWithSystem, Port, UploadLimitBytesPerSecond, ParallelTransfers, VerboseLogging,
            HashCode.Combine(StunServers.Count + PathVariables.Count, RendezvousUrl));
}

/// <summary>Checks and normalizes rendezvous service URLs.</summary>
public static class RendezvousUrls
{
    public const string DefaultPath = "/v1";

    /// <summary>
    /// Accepts <c>wss://</c> and <c>ws://</c> (and <c>https://</c>/<c>http://</c>, turned into the WebSocket form); a
    /// URL without path gets <c>/v1</c>. Blank or invalid text gives false.
    /// </summary>
    public static bool TryNormalize(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
            return false;
        var scheme = uri.Scheme switch
        {
            "wss" or "https" => "wss",
            "ws" or "http" => "ws",
            _ => null,
        };
        if (scheme is null || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var builder = new UriBuilder(uri) { Scheme = scheme, Port = uri.IsDefaultPort ? -1 : uri.Port };
        if (builder.Path is "" or "/")
            builder.Path = DefaultPath;
        url = builder.Uri.ToString();
        return true;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
