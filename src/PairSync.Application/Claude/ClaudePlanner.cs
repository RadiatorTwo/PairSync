using System.Text.Json.Nodes;

namespace PairSync.Application.Claude;

/// <summary>A portable entry compared with the target. For <c>settings.json</c> the counts are top-level keys.</summary>
public sealed record PortableEntry(PortableItem Item, int Added, int Changed, int Same, int OnlyOnTarget, IReadOnlyList<ClaudeFile> ToSend)
{
    public bool HasChanges => Added + Changed > 0;
}

public enum ExecutableKind
{
    Hook,
    Setting,
    McpServer,
    Plugin,
    Installer,
}

/// <summary>Something that runs programs on the target; applied only after the user confirmed it (plan §9).</summary>
/// <param name="Id"><c>hook:Event</c>, <c>setting:key</c>, <c>mcp:name</c> or <c>plugin:id</c>.</param>
/// <param name="UnmappedPaths">Absolute paths that are not below the source's folders; the user can map them.</param>
public sealed record ExecutableItem(string Id, ExecutableKind Kind, string Title, string Command, IReadOnlyList<string> UnmappedPaths);

/// <summary>A top-level key of <c>settings.json</c> (or one hook event if <see cref="SubKey"/> is set) the target takes over.</summary>
public sealed record SettingChange(string Key, string? SubKey, JsonNode Value, string? ExecutableId);

public enum ClaudeStepKind
{
    MarketplaceAdd = 0,
    PluginInstall = 1,
    PluginEnable = 2,
    PluginDisable = 3,
    McpAdd = 4,

    /// <summary>Installs Claude Code itself with the official installer and puts it on PATH (protocol 0.7).</summary>
    InstallClaude = 5,

    /// <summary>Installs a tool of <see cref="ClaudeTools.Names"/> with the system's package manager (protocol 0.8).</summary>
    InstallTool = 6,
}

/// <summary>A CLI step on the target. The target builds the command line itself from these fields.</summary>
/// <param name="Name">Marketplace, plugin id or MCP server name.</param>
/// <param name="Value">Marketplace source or MCP JSON.</param>
public sealed record ClaudeStep(ClaudeStepKind Kind, string Name, string? Value, string? ExecutableId, string Meta = "");

public enum McpKind
{
    Http,
    PackageRunner,
    AbsolutePath,
    Command,
}

public sealed record McpPlan(string Name, McpKind Kind, IReadOnlyList<string> SecretVariables, bool Unchanged);

public sealed record ClaudePlan(
    IReadOnlyList<PortableEntry> Entries,
    IReadOnlyList<SettingChange> Settings,
    IReadOnlyList<ClaudeStep> Steps,
    IReadOnlyList<ExecutableItem> Executables,
    IReadOnlyList<McpPlan> McpServers,
    IReadOnlyList<string> SyncedPlugins,
    IReadOnlyList<string> Notes);

/// <summary>What the user chose on the Claude Code page.</summary>
/// <param name="PathMappings">Original absolute path → what the target gets instead.</param>
public sealed record ClaudeSelection(IReadOnlySet<string> Entries, IReadOnlySet<string> ConfirmedExecutables, IReadOnlyDictionary<string, string> PathMappings);

/// <summary>What the source sends: files to write, <c>settings.json</c> keys and CLI steps.</summary>
public sealed record ClaudeApplyRequest(IReadOnlyList<ClaudeFile> Files, IReadOnlyList<SettingChange> Settings, IReadOnlyList<ClaudeStep> Steps);

/// <summary><c>settings.json</c> and MCP servers in the form both devices compare: secrets removed, own folders as variables.</summary>
public sealed record ComparableConfig(
    JsonObject Settings,
    IReadOnlyDictionary<string, JsonObject> McpServers,
    IReadOnlyDictionary<string, IReadOnlyList<string>> UnmappedPaths,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SecretVariables);

/// <summary>Compares the source with the target and builds the plan. Pure: no files, no processes.</summary>
public static class ClaudePlanner
{
    /// <summary>Keys of <c>settings.json</c> whose values Claude Code runs as commands (plus <c>hooks</c>, per event).</summary>
    public static IReadOnlySet<string> ExecutableSettings { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "hooks", "statusLine", "apiKeyHelper", "awsAuthRefresh", "awsCredentialExport", "otelHeadersHelper", "fileSuggestion",
    };

    /// <summary>Carried by the plugin plan instead of being copied.</summary>
    public static IReadOnlySet<string> PluginSettings { get; } = new HashSet<string>(StringComparer.Ordinal) { "enabledPlugins", "extraKnownMarketplaces" };

    private static readonly HashSet<string> PackageRunners = new(["npx", "uvx", "docker", "podman", "bunx", "pipx", "pnpm", "yarn", "deno"], StringComparer.OrdinalIgnoreCase);

    public static ComparableConfig Comparable(ClaudeSnapshot snapshot)
    {
        var windows = snapshot.Os == "windows";
        var unmapped = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var settings = SecretDetector.RedactSettings(snapshot.Settings);
        foreach (var key in ExecutableSettings)
        {
            if (settings[key] is not { } value)
                continue;
            if (key == "hooks" && value is JsonObject events)
            {
                foreach (var (name, hooks) in events.ToList())
                {
                    var paths = new List<string>();
                    events[name] = GeneralizeStrings(hooks, snapshot.Environment, windows, paths);
                    unmapped[$"hook:{name}"] = paths;
                }
            }
            else
            {
                var paths = new List<string>();
                settings[key] = GeneralizeStrings(value, snapshot.Environment, windows, paths);
                unmapped[$"setting:{key}"] = paths;
            }
        }

        var servers = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var secrets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, server) in snapshot.McpServers)
        {
            var variables = new List<string>();
            var copy = SecretDetector.RedactMcpServer(name, server, variables);
            var paths = new List<string>();
            if (copy["command"] is JsonValue command && command.TryGetValue<string>(out var text))
                copy["command"] = PathMapper.Generalize(text, snapshot.Environment, windows, paths, wholeValue: true);
            if (copy["args"] is JsonArray args)
            {
                for (var i = 0; i < args.Count; i++)
                {
                    if (args[i] is JsonValue arg && arg.TryGetValue<string>(out var argText))
                        args[i] = PathMapper.Generalize(argText, snapshot.Environment, windows, paths, wholeValue: true);
                }
            }
            servers[name] = copy;
            unmapped[$"mcp:{name}"] = paths;
            secrets[name] = variables;
        }
        return new ComparableConfig(settings, servers, unmapped, secrets);
    }

    private static JsonNode? GeneralizeStrings(JsonNode? node, ClaudeEnvironment environment, bool windows, List<string> unmapped) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, GeneralizeStrings(p.Value, environment, windows, unmapped)))),
        JsonArray array => new JsonArray([.. array.Select(v => GeneralizeStrings(v, environment, windows, unmapped))]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(PathMapper.Generalize(text, environment, windows, unmapped)),
        _ => node?.DeepClone(),
    };

    /// <param name="target">The target as it reported itself: settings and MCP servers already in comparable form.</param>
    public const string InstallClaudeId = "install:claude";

    public static string ToolId(string tool) => "tool:" + tool;

    /// <param name="offerInstall">The target can install Claude Code itself (protocol 0.7); offered when it is missing there.</param>
    public static ClaudePlan Plan(ClaudeSnapshot source, ClaudeSnapshot target, bool offerInstall = false)
    {
        var comparable = Comparable(source);
        var notes = new List<string>();
        var executables = new List<ExecutableItem>();
        var settings = PlanSettings(comparable, target.Settings, executables, out var settingsEntry);

        var entries = new List<PortableEntry> { settingsEntry };
        var targetFiles = target.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var item in PortableItem.All.Where(i => i.Key != PortableItem.SettingsKey))
        {
            int added = 0, changed = 0, same = 0;
            var send = new List<ClaudeFile>();
            foreach (var file in source.Files.Where(f => item.Contains(f.Path)))
            {
                if (!targetFiles.TryGetValue(file.Path, out var there))
                {
                    added++;
                    send.Add(file);
                }
                else if (there.Size != file.Size || !there.Sha256.AsSpan().SequenceEqual(file.Sha256))
                {
                    changed++;
                    send.Add(file);
                }
                else
                {
                    same++;
                }
            }
            var sourcePaths = source.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var onlyThere = target.Files.Count(f => item.Contains(f.Path) && !sourcePaths.Contains(f.Path));
            entries.Add(new PortableEntry(item, added, changed, same, onlyThere, send));
        }
        if (source.SkippedFiles.Count > 0)
            notes.Add($"Not sent (over {ClaudeInventory.MaxFileSize / 1024 / 1024} MB or over {ClaudeInventory.MaxFiles} files): {string.Join(", ", source.SkippedFiles.Take(5))}{(source.SkippedFiles.Count > 5 ? ", …" : "")}");

        var steps = new List<ClaudeStep>();
        var synced = PlanPlugins(source, target, steps, executables, notes);
        var mcp = PlanMcp(comparable, target.McpServers, steps, executables);
        if (offerInstall && target.Version is null)
        {
            // First, so the CLI steps after it find the program.
            executables.Insert(0, new ExecutableItem(InstallClaudeId, ExecutableKind.Installer, "Install Claude Code",
                target.Os == "windows" ? "irm https://claude.ai/install.ps1 | iex  (+ PATH)" : "curl -fsSL https://claude.ai/install.sh | bash  (+ PATH)", []));
            steps.Insert(0, new ClaudeStep(ClaudeStepKind.InstallClaude, "claude-code", null, InstallClaudeId, "official installer · user"));
        }
        // Tools the target reports missing (0.8 and later): git for marketplaces, bun and jq for plugin hooks.
        foreach (var tool in (target.MissingTools ?? []).Reverse())
        {
            var how = target.Os == "windows" ? "winget install"
                : tool == "bun" ? "official installer into ~/.bun"
                : tool == "warp" ? "package from warp.dev (asks for the password there)"
                : "package manager (asks for the password there)";
            executables.Insert(0, new ExecutableItem(ToolId(tool), ExecutableKind.Installer, $"Install {tool}", $"{how}: {tool}", []));
            steps.Insert(0, new ClaudeStep(ClaudeStepKind.InstallTool, tool, null, ToolId(tool), "for Claude Code plugins"));
        }
        return new ClaudePlan(entries, settings, steps, executables, mcp, synced, notes);
    }

    private static List<SettingChange> PlanSettings(ComparableConfig source, JsonObject target, List<ExecutableItem> executables, out PortableEntry entry)
    {
        var changes = new List<SettingChange>();
        int added = 0, changed = 0, same = 0;
        foreach (var (key, value) in source.Settings)
        {
            if (PluginSettings.Contains(key) || value is null)
                continue;
            if (key == "hooks" && value is JsonObject events)
            {
                var targetEvents = target["hooks"] as JsonObject;
                foreach (var (name, hooks) in events)
                {
                    if (hooks is null || JsonNode.DeepEquals(hooks, targetEvents?[name]))
                        continue;
                    var id = $"hook:{name}";
                    changes.Add(new SettingChange(key, name, hooks.DeepClone(), id));
                    executables.Add(new ExecutableItem(id, ExecutableKind.Hook, $"Hook · {name}", string.Join("\n", Commands(hooks)), source.UnmappedPaths[id]));
                }
                continue;
            }
            if (JsonNode.DeepEquals(value, target[key]))
            {
                if (!ExecutableSettings.Contains(key))
                    same++;
                continue;
            }
            if (ExecutableSettings.Contains(key))
            {
                var id = $"setting:{key}";
                changes.Add(new SettingChange(key, null, value.DeepClone(), id));
                executables.Add(new ExecutableItem(id, ExecutableKind.Setting, key, string.Join("\n", Commands(value)), source.UnmappedPaths[id]));
                continue;
            }
            if (target.ContainsKey(key))
                changed++;
            else
                added++;
            changes.Add(new SettingChange(key, null, value.DeepClone(), null));
        }
        var onlyThere = target.Count(p => !PluginSettings.Contains(p.Key) && !ExecutableSettings.Contains(p.Key) && !source.Settings.ContainsKey(p.Key));
        entry = new PortableEntry(PortableItem.All[0], added, changed, same, onlyThere, []);
        return changes;
    }

    /// <summary>The command strings in a hook list or setting value, for display.</summary>
    private static IEnumerable<string> Commands(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => [text],
        JsonObject obj when obj["command"] is JsonValue command && command.TryGetValue<string>(out var text) => [text],
        JsonObject obj => obj.Where(p => p.Value is JsonObject or JsonArray).SelectMany(p => Commands(p.Value)),
        JsonArray array => array.SelectMany(Commands),
        _ => [],
    };

    private static List<string> PlanPlugins(ClaudeSnapshot source, ClaudeSnapshot target, List<ClaudeStep> steps, List<ExecutableItem> executables, List<string> notes)
    {
        var synced = new List<string>();
        var targetPlugins = target.Plugins.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var targetMarketplaces = target.Marketplaces.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var marketplaces = source.Marketplaces.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);

        foreach (var plugin in source.Plugins)
        {
            if (plugin.Synced)
            {
                synced.Add(plugin.Id);
                continue;
            }
            targetPlugins.TryGetValue(plugin.Id, out var there);
            if (!plugin.Enabled)
            {
                if (there is { Enabled: true })
                    steps.Add(new ClaudeStep(ClaudeStepKind.PluginDisable, plugin.Id, null, null, "disabled in enabledPlugins"));
                continue;
            }
            if (there is { Installed: true, Enabled: true })
                continue;

            var marketplace = marketplaces.GetValueOrDefault(plugin.Marketplace);
            var autoUpdate = marketplace?.AutoUpdate switch { true => " · auto-update on", false => " · auto-update off", null => "" };
            var meta = $"enabledPlugins{(plugin.Version is null ? "" : $" · {plugin.Version}")}{autoUpdate}";
            var id = $"plugin:{plugin.Id}";
            if (there is { Installed: true })
            {
                steps.Add(new ClaudeStep(ClaudeStepKind.PluginEnable, plugin.Id, null, id, meta));
                executables.Add(new ExecutableItem(id, ExecutableKind.Plugin, $"Enable plugin {plugin.Id}", $"claude plugin enable {plugin.Id}", []));
                continue;
            }
            if (!targetMarketplaces.Contains(plugin.Marketplace) && !added.Contains(plugin.Marketplace))
            {
                if (marketplace is null || MarketplaceArgument(marketplace.Source) is not { } argument)
                {
                    notes.Add($"{plugin.Id}: the marketplace {plugin.Marketplace} is not available as a GitHub or Git source; add it on the target by hand.");
                    continue;
                }
                steps.Add(new ClaudeStep(ClaudeStepKind.MarketplaceAdd, plugin.Marketplace, argument, null, autoUpdate.TrimStart(' ', '·')));
                added.Add(plugin.Marketplace);
            }
            steps.Add(new ClaudeStep(ClaudeStepKind.PluginInstall, plugin.Id, null, id, meta));
            executables.Add(new ExecutableItem(id, ExecutableKind.Plugin, $"Plugin {plugin.Id}", $"claude plugin install {plugin.Id} --scope user", []));
        }
        return synced;
    }

    /// <summary>The argument for <c>claude plugin marketplace add</c>; null for local folders and unknown kinds.</summary>
    public static string? MarketplaceArgument(JsonObject source)
    {
        string? Text(string key) => source[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var reference = Text("ref");
        var argument = Text("source") switch
        {
            "github" => Text("repo"),
            "git" or "url" => Text("url"),
            _ => null,
        };
        if (argument is null)
            return null;
        if (reference is { Length: > 0 })
            argument += "#" + reference;
        return IsSafeArgument(argument) ? argument : null;
    }

    /// <summary>No option look-alikes, whitespace or control characters in a value passed to the CLI.</summary>
    public static bool IsSafeArgument(string value) =>
        value.Length is > 0 and <= 512 && !value.StartsWith('-') && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static List<McpPlan> PlanMcp(ComparableConfig source, IReadOnlyDictionary<string, JsonObject> target, List<ClaudeStep> steps, List<ExecutableItem> executables)
    {
        var plans = new List<McpPlan>();
        foreach (var (name, server) in source.McpServers.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var kind = Classify(server);
            var unchanged = target.TryGetValue(name, out var there) && JsonNode.DeepEquals(server, there);
            plans.Add(new McpPlan(name, kind, source.SecretVariables[name], unchanged));
            if (unchanged)
                continue;
            var id = kind == McpKind.Http ? null : $"mcp:{name}";
            steps.Add(new ClaudeStep(ClaudeStepKind.McpAdd, name, server.ToJsonString(), id, kind.ToString()));
            if (id is not null)
            {
                var command = string.Join(' ', new[] { Text(server["command"]) }.Concat((server["args"] as JsonArray ?? []).Select(Text)));
                executables.Add(new ExecutableItem(id, ExecutableKind.McpServer, $"MCP server {name}", command, source.UnmappedPaths[id]));
            }
        }
        return plans;

        static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    }

    public static McpKind Classify(JsonObject server)
    {
        if (server["command"] is not JsonValue value || !value.TryGetValue<string>(out var command) || command.Length == 0)
            return McpKind.Http;
        if (PackageRunners.Contains(Path.GetFileNameWithoutExtension(command.Replace('\\', '/').Split('/')[^1])))
            return McpKind.PackageRunner;
        return command.Contains('/') || command.Contains('\\') ? McpKind.AbsolutePath : McpKind.Command;
    }

    /// <summary>Keeps what the user ticked and confirmed and applies the path mappings.</summary>
    public static ClaudeApplyRequest BuildApply(ClaudePlan plan, ClaudeSelection selection)
    {
        bool Allowed(string? executableId) => executableId is null || selection.ConfirmedExecutables.Contains(executableId);

        var files = plan.Entries.Where(e => selection.Entries.Contains(e.Item.Key)).SelectMany(e => e.ToSend).ToList();
        var settings = plan.Settings
            .Where(c => c.ExecutableId is null ? selection.Entries.Contains(PortableItem.SettingsKey) : Allowed(c.ExecutableId))
            .Select(c => c.ExecutableId is null ? c : c with { Value = MapStrings(c.Value, selection.PathMappings)! })
            .ToList();
        var steps = plan.Steps.Where(s => s.Kind != ClaudeStepKind.MarketplaceAdd && Allowed(s.ExecutableId))
            .Select(s => s is { Kind: ClaudeStepKind.McpAdd, Value: { } json }
                ? s with { Value = MapStrings(Parse(json), selection.PathMappings)!.ToJsonString() }
                : s)
            .ToList();
        // A marketplace is only added for a plugin that is installed from it.
        var needed = steps.Where(s => s.Kind == ClaudeStepKind.PluginInstall).Select(s => s.Name[(s.Name.LastIndexOf('@') + 1)..]).ToHashSet(StringComparer.Ordinal);
        steps.InsertRange(0, plan.Steps.Where(s => s.Kind == ClaudeStepKind.MarketplaceAdd && needed.Contains(s.Name)));
        // Tools first, then Claude Code itself, then the CLI steps.
        steps = [.. steps.OrderBy(s => s.Kind switch { ClaudeStepKind.InstallTool => 0, ClaudeStepKind.InstallClaude => 1, _ => 2 })];
        return new ClaudeApplyRequest(files, settings, steps);

        static JsonNode Parse(string json) => JsonNode.Parse(json)!;
    }

    private static JsonNode? MapStrings(JsonNode? node, IReadOnlyDictionary<string, string> mappings) => node switch
    {
        _ when mappings.Count == 0 => node,
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, MapStrings(p.Value, mappings)))),
        JsonArray array => new JsonArray([.. array.Select(v => MapStrings(v, mappings))]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(PathMapper.ApplyMappings(text, mappings)),
        _ => node?.DeepClone(),
    };
}
