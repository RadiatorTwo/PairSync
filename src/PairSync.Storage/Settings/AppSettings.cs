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
        && StunServers.SequenceEqual(other.StunServers);

    public override int GetHashCode() =>
        HashCode.Combine(DeviceName, CloseBehavior, StartWithSystem, Port, UploadLimitBytesPerSecond, ParallelTransfers, VerboseLogging,
            StunServers.Count);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
