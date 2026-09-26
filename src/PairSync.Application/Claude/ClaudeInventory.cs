using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PairSync.Application.Claude;

/// <summary>A portable entry of the configuration folder (plan §9 "Standardmäßig synchronisieren").</summary>
/// <param name="Paths">Relative to the configuration folder; a trailing <c>/</c> marks a folder.</param>
public sealed record PortableItem(string Key, string Title, bool DefaultOn, IReadOnlyList<string> Paths)
{
    public const string SettingsKey = "settings";

    public static IReadOnlyList<PortableItem> All { get; } =
    [
        new(SettingsKey, "settings.json", true, ["settings.json"]),
        new("claude-md", "CLAUDE.md", true, ["CLAUDE.md"]),
        new("skills", "skills/", true, ["skills/"]),
        new("agents", "agents/", true, ["agents/"]),
        new("rules", "rules/ · commands/", true, ["rules/", "commands/"]),
        new("hooks", "hooks/", true, ["hooks/"]),
        new("output-styles", "output-styles/", true, ["output-styles/"]),
        new("workflows", "workflows/", true, ["workflows/"]),
        new("themes", "themes/ · keybindings.json", false, ["themes/", "keybindings.json"]),
        new("agent-memory", "agent-memory/", false, ["agent-memory/"]),
    ];

    public bool Contains(string path) =>
        Paths.Any(p => p.EndsWith('/') ? path.StartsWith(p, StringComparison.Ordinal) : path == p);

    /// <summary>The entry a relative file path belongs to; null for anything outside the portable entries.</summary>
    public static PortableItem? For(string path) => All.FirstOrDefault(i => i.Key != SettingsKey && i.Contains(path));
}

/// <param name="Path">Relative to the configuration folder, with <c>/</c>.</param>
public sealed record ClaudeFile(string Path, long Size, byte[] Sha256);

/// <param name="Installed">Listed in <c>installed_plugins.json</c>; an enabled plugin can be missing there.</param>
/// <param name="Synced">Managed by the claude.ai account (<c>name@synced</c>); never transferred.</param>
public sealed record ClaudePlugin(string Id, bool Installed, bool Enabled, string? Version, bool Synced)
{
    public string Marketplace => Id[(Id.LastIndexOf('@') + 1)..];
}

/// <param name="Source">The <c>source</c> object as Claude Code stores it (<c>github</c>, <c>git</c>, <c>url</c>, <c>directory</c>, …).</param>
public sealed record ClaudeMarketplace(string Name, JsonObject Source, bool? AutoUpdate);

/// <summary>What the Claude Code provider knows about one device.</summary>
public sealed record ClaudeSnapshot
{
    public required ClaudeEnvironment Environment { get; init; }

    public string? Version { get; init; }

    /// <summary>"windows", "linux" or "macos".</summary>
    public required string Os { get; init; }

    public IReadOnlyList<ClaudeFile> Files { get; init; } = [];

    /// <summary>Files left out because of their size or the file limit.</summary>
    public IReadOnlyList<string> SkippedFiles { get; init; } = [];

    public JsonObject Settings { get; init; } = [];

    public IReadOnlyList<ClaudePlugin> Plugins { get; init; } = [];

    public IReadOnlyList<ClaudeMarketplace> Marketplaces { get; init; } = [];

    public IReadOnlyDictionary<string, JsonObject> McpServers { get; init; } = new Dictionary<string, JsonObject>();

    public static string CurrentOs => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
}

/// <summary>Reads the portable part of a Claude Code installation. Never reads credentials, history or caches.</summary>
public static class ClaudeInventory
{
    public const long MaxFileSize = 64L * 1024 * 1024;
    public const int MaxFiles = 5000;

    private static readonly HashSet<string> IgnoredNames =
        new(["node_modules", ".git", "__pycache__", ".DS_Store", "Thumbs.db", "desktop.ini"], StringComparer.OrdinalIgnoreCase);

    internal static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static ClaudeSnapshot Read(ClaudeEnvironment environment, string? version)
    {
        var skipped = new List<string>();
        var settings = ReadObject(Path.Combine(environment.ConfigDir, "settings.json")) ?? [];
        return new ClaudeSnapshot
        {
            Environment = environment,
            Version = version,
            Os = ClaudeSnapshot.CurrentOs,
            Files = ReadFiles(environment.ConfigDir, skipped),
            SkippedFiles = skipped,
            Settings = settings,
            Plugins = ReadPlugins(environment.ConfigDir, settings),
            Marketplaces = ReadMarketplaces(environment.ConfigDir, settings),
            McpServers = ReadMcpServers(environment.GlobalConfigFile),
        };
    }

    /// <summary>A JSON object from a file; null if the file is missing or not a JSON object.</summary>
    public static JsonObject? ReadObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: JsonOptions) as JsonObject : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<ClaudeFile> ReadFiles(string configDir, List<string> skipped)
    {
        var files = new List<ClaudeFile>();
        foreach (var item in PortableItem.All)
        {
            if (item.Key == PortableItem.SettingsKey)
                continue;
            foreach (var path in item.Paths)
            {
                var full = Path.Combine(configDir, path.TrimEnd('/'));
                if (path.EndsWith('/'))
                {
                    if (Directory.Exists(full) && !IsLink(full))
                        AddFolder(configDir, full, files, skipped);
                }
                else if (File.Exists(full) && !IsLink(full))
                {
                    AddFile(path, full, files, skipped);
                }
            }
        }
        return files;
    }

    private static void AddFolder(string configDir, string folder, List<ClaudeFile> files, List<string> skipped)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var entry in entries)
        {
            if (IgnoredNames.Contains(entry.Name) || entry.LinkTarget is not null)
                continue;
            if (entry is DirectoryInfo directory)
                AddFolder(configDir, directory.FullName, files, skipped);
            else
                AddFile(Path.GetRelativePath(configDir, entry.FullName).Replace('\\', '/'), entry.FullName, files, skipped);
        }
    }

    private static void AddFile(string relative, string full, List<ClaudeFile> files, List<string> skipped)
    {
        try
        {
            var info = new FileInfo(full);
            if (info.Length > MaxFileSize || files.Count >= MaxFiles)
            {
                skipped.Add(relative);
                return;
            }
            using var stream = File.OpenRead(full);
            files.Add(new ClaudeFile(relative, info.Length, SHA256.HashData(stream)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            skipped.Add(relative);
        }
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static List<ClaudePlugin> ReadPlugins(string configDir, JsonObject settings)
    {
        var enabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (settings["enabledPlugins"] is JsonObject map)
        {
            foreach (var (id, value) in map)
                enabled[id] = value is JsonValue v && v.TryGetValue<bool>(out var on) && on;
        }

        var installed = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (ReadObject(Path.Combine(configDir, "plugins", "installed_plugins.json"))?["plugins"] is JsonObject plugins)
        {
            foreach (var (id, entries) in plugins)
            {
                // Version 2 lists installations per scope; only the user scope is portable.
                var user = (entries as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(e => (string?)e["scope"] is null or "user");
                if (user is not null)
                    installed[id] = user["version"] is JsonValue v && v.TryGetValue<string>(out var version) ? version : null;
            }
        }

        return [.. enabled.Keys.Union(installed.Keys).Where(IsPluginId).Order(StringComparer.Ordinal).Select(id => new ClaudePlugin(
            id, installed.ContainsKey(id), enabled.GetValueOrDefault(id), installed.GetValueOrDefault(id),
            id.EndsWith("@synced", StringComparison.Ordinal)))];
    }

    private static List<ClaudeMarketplace> ReadMarketplaces(string configDir, JsonObject settings)
    {
        var result = new Dictionary<string, ClaudeMarketplace>(StringComparer.Ordinal);
        void Add(JsonObject? map)
        {
            if (map is null)
                return;
            foreach (var (name, value) in map)
            {
                if (value is JsonObject entry && entry["source"] is JsonObject source && IsName(name) && !result.ContainsKey(name))
                {
                    bool? autoUpdate = entry["autoUpdate"] is JsonValue v && v.TryGetValue<bool>(out var on) ? on : null;
                    result[name] = new ClaudeMarketplace(name, (JsonObject)source.DeepClone(), autoUpdate);
                }
            }
        }
        Add(ReadObject(Path.Combine(configDir, "plugins", "known_marketplaces.json")));
        Add(settings["extraKnownMarketplaces"] as JsonObject);
        return [.. result.Values.OrderBy(m => m.Name, StringComparer.Ordinal)];
    }

    private static Dictionary<string, JsonObject> ReadMcpServers(string globalConfigFile)
    {
        // Only mcpServers is read from .claude.json; everything else in it is local state (plan §9).
        var servers = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (ReadObject(globalConfigFile)?["mcpServers"] is JsonObject map)
        {
            foreach (var (name, value) in map)
            {
                if (value is JsonObject server && IsName(name))
                    servers[name] = (JsonObject)server.DeepClone();
            }
        }
        return servers;
    }

    /// <summary>Names of marketplaces and MCP servers: letters, digits, <c>-_.</c>; nothing an argument parser could misread.</summary>
    public static bool IsName(string name) =>
        name.Length is > 0 and <= 128 && !name.StartsWith('-') && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary><c>plugin@marketplace</c> with two valid names.</summary>
    public static bool IsPluginId(string id)
    {
        var at = id.LastIndexOf('@');
        return at > 0 && IsName(id[..at]) && IsName(id[(at + 1)..]);
    }
}
