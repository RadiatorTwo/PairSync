using System.Text.Json;
using System.Text.Json.Nodes;
using PairSync.Protocol;

namespace PairSync.Application.Claude;

/// <summary>Conversions between the provider's model and the protocol 0.6 messages.</summary>
internal static class ClaudeWire
{
    public static ClaudeFileEntry ToWire(this ClaudeFile file) => new() { Path = file.Path, Size = file.Size, Sha256 = file.Sha256 };

    public static ClaudeState ToState(ClaudeSnapshot snapshot, bool allowPrograms, IEnumerable<string> pathVariableNames)
    {
        var comparable = ClaudePlanner.Comparable(snapshot);
        return new ClaudeState
        {
            Allowed = true,
            AllowPrograms = allowPrograms,
            ConfigDir = snapshot.Environment.ConfigDir,
            ConfigDirFromEnv = snapshot.Environment.ConfigDirFromEnv,
            ClaudeVersion = snapshot.Version,
            Os = snapshot.Os,
            HomeDir = snapshot.Environment.HomeDir,
            Files = [.. snapshot.Files.Select(ToWire)],
            SettingsJson = comparable.Settings.ToJsonString(),
            Plugins = [.. snapshot.Plugins.Select(p => new ClaudePluginEntry { Id = p.Id, Installed = p.Installed, Enabled = p.Enabled, Version = p.Version })],
            Marketplaces = [.. snapshot.Marketplaces.Select(m => m.Name)],
            McpServers = [.. comparable.McpServers.Select(s => new ClaudeMcpEntry { Name = s.Key, Json = s.Value.ToJsonString() })],
            PathVariables = [.. pathVariableNames],
            MissingTools = snapshot.MissingTools is { } missing ? [.. missing] : null,
        };
    }

    /// <summary>The target as the planner needs it; settings and MCP servers are already in comparable form.</summary>
    public static ClaudeSnapshot FromState(ClaudeState state)
    {
        var configDir = state.ConfigDir ?? "";
        return new ClaudeSnapshot
        {
            Environment = new ClaudeEnvironment(configDir, state.ConfigDirFromEnv, "", state.HomeDir ?? ""),
            Version = state.ClaudeVersion,
            Os = state.Os ?? "linux",
            Files = [.. (state.Files ?? []).Where(f => f.Path is not null && f.Sha256 is { Length: 32 }).Select(f => new ClaudeFile(f.Path!, f.Size, f.Sha256!))],
            Settings = ParseObject(state.SettingsJson) ?? [],
            Plugins = [.. (state.Plugins ?? []).Where(p => p.Id is not null && ClaudeInventory.IsPluginId(p.Id))
                .Select(p => new ClaudePlugin(p.Id!, p.Installed, p.Enabled, p.Version, p.Id!.EndsWith("@synced", StringComparison.Ordinal)))],
            Marketplaces = [.. (state.Marketplaces ?? []).Where(ClaudeInventory.IsName).Select(n => new ClaudeMarketplace(n, [], null))],
            McpServers = (state.McpServers ?? [])
                .Where(s => s.Name is not null && ClaudeInventory.IsName(s.Name))
                .Select(s => (s.Name!, Json: ParseObject(s.Json)))
                .Where(s => s.Json is not null)
                .GroupBy(s => s.Item1, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Json!, StringComparer.Ordinal),
            MissingTools = state.MissingTools is { } missing ? [.. missing.Where(ClaudeTools.IsKnown).Distinct(StringComparer.Ordinal)] : null,
        };
    }

    public static ClaudeApply ToWire(ClaudeApplyRequest request) => new()
    {
        Files = [.. request.Files.Select(ToWire)],
        Settings = [.. request.Settings.Select(s => new ClaudeSettingEntry { Key = s.Key, SubKey = s.SubKey, Json = s.Value.ToJsonString() })],
        Steps = [.. request.Steps.Select(s => new ClaudeStepEntry { Kind = (int)s.Kind, Name = s.Name, Value = s.Value, Title = StepTitle(s) })],
    };

    public static string StepTitle(ClaudeStep step) => ClaudeStepText.Of(step);

    public static JsonObject? ParseObject(string? json)
    {
        if (json is null)
            return null;
        try
        {
            return JsonNode.Parse(json, documentOptions: ClaudeInventory.JsonOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class ClaudeStepText
{
    /// <summary>The step as the page and the log show it: "marketplace add org/m#v1.2.0", "install a@m · user".</summary>
    public static string Of(ClaudeStep step) => step.Kind switch
    {
        ClaudeStepKind.MarketplaceAdd => $"marketplace add {step.Value}",
        ClaudeStepKind.PluginInstall => $"install {step.Name} · user",
        ClaudeStepKind.PluginEnable => $"enable {step.Name}",
        ClaudeStepKind.PluginDisable => $"disable {step.Name}",
        ClaudeStepKind.InstallClaude => "install Claude Code · official installer + PATH",
        ClaudeStepKind.InstallTool => $"install {step.Name}",
        _ => $"mcp add-json {step.Name}",
    };
}
