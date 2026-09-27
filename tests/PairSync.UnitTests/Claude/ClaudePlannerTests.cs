using System.Text.Json.Nodes;
using PairSync.Application.Claude;

namespace PairSync.UnitTests.Claude;

public sealed class PathMapperTests
{
    private static readonly ClaudeEnvironment Windows = new(@"C:\Users\Radi\.claude", false, @"C:\Users\Radi\.claude.json", @"C:\Users\Radi");
    private static readonly ClaudeEnvironment Linux = new("/home/bob/.claude", false, "/home/bob/.claude.json", "/home/bob");

    [Fact]
    public void Own_folders_become_variables_other_paths_are_reported()
    {
        var unmapped = new List<string>();
        var text = PathMapper.Generalize(
            @"node 'C:\Users\Radi\.claude\scripts\statusline.js' && C:/Users/Radi/.local/bin/snip.exe hook && D:\tools\lt.exe --url https://x.dev/a",
            Windows, windows: true, unmapped);
        Assert.Equal(@"node '${CLAUDE_CONFIG_DIR}/scripts/statusline.js' && ${HOME}/.local/bin/snip.exe hook && D:\tools\lt.exe --url https://x.dev/a", text);
        Assert.Equal([@"D:\tools\lt.exe"], unmapped);
    }

    [Fact]
    public void Case_matters_only_on_windows_and_prefixes_must_end_at_a_separator()
    {
        var unmapped = new List<string>();
        Assert.Equal("${HOME}/x", PathMapper.Generalize(@"c:\users\radi\x", Windows, true, unmapped));
        Assert.Equal("/home/bobby/x", PathMapper.Generalize("/home/bobby/x", Linux, false, unmapped));
        Assert.Equal("/HOME/bob/x", PathMapper.Generalize("/HOME/bob/x", Linux, false, unmapped));
        Assert.Equal(["/home/bobby/x", "/HOME/bob/x"], unmapped);
    }

    [Fact]
    public void Whole_values_keep_spaces_and_relative_words_are_left_alone()
    {
        var unmapped = new List<string>();
        Assert.Equal("${HOME}/My Tools/x.exe", PathMapper.Generalize(@"C:\Users\Radi\My Tools\x.exe", Windows, true, unmapped, wholeValue: true));
        Assert.Equal("bin/run and ./x and ${CLAUDE_PLUGIN_ROOT}/y", PathMapper.Generalize("bin/run and ./x and ${CLAUDE_PLUGIN_ROOT}/y", Linux, false, unmapped));
        Assert.Empty(unmapped);
    }

    [Fact]
    public void Target_expands_known_variables_only()
    {
        var variables = PathMapper.TargetVariables(Linux, ["TOOLS_ROOT = /opt/tools", "bad line", "1X=no"]);
        Assert.Equal("/home/bob/.claude/a.js ${TOKEN} /opt/tools/lt ${CLAUDE_PLUGIN_ROOT}",
            PathMapper.Expand("${CLAUDE_CONFIG_DIR}/a.js ${TOKEN} ${TOOLS_ROOT}/lt ${CLAUDE_PLUGIN_ROOT}", variables));
        Assert.Equal("C:/Users/Bob/x", PathMapper.Expand("${HOME}/x", new Dictionary<string, string> { ["HOME"] = @"C:\Users\Bob\" }));
    }
}

public sealed class ClaudePlannerTests
{
    private static readonly ClaudeEnvironment SourceEnv = new("/home/a/.claude", false, "/home/a/.claude.json", "/home/a");
    private static readonly ClaudeEnvironment TargetEnv = new(@"C:\Users\B\.claude", false, @"C:\Users\B\.claude.json", @"C:\Users\B");

    private static ClaudeFile F(string path, string content) =>
        new(path, content.Length, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private static ClaudeSnapshot Source(JsonObject? settings = null, ClaudeFile[]? files = null, ClaudePlugin[]? plugins = null,
        ClaudeMarketplace[]? marketplaces = null, Dictionary<string, JsonObject>? mcp = null) => new()
    {
        Environment = SourceEnv, Os = "linux", Settings = settings ?? [], Files = files ?? [], Plugins = plugins ?? [],
        Marketplaces = marketplaces ?? [], McpServers = mcp ?? [],
    };

    /// <summary>The target as it reports itself: already in comparable form.</summary>
    private static ClaudeSnapshot Target(JsonObject? settings = null, ClaudeFile[]? files = null, ClaudePlugin[]? plugins = null,
        ClaudeMarketplace[]? marketplaces = null, Dictionary<string, JsonObject>? mcp = null)
    {
        var raw = new ClaudeSnapshot
        {
            Environment = TargetEnv, Os = "windows", Settings = settings ?? [], Files = files ?? [], Plugins = plugins ?? [],
            Marketplaces = marketplaces ?? [], McpServers = mcp ?? [],
        };
        var comparable = ClaudePlanner.Comparable(raw);
        return raw with { Settings = comparable.Settings, McpServers = comparable.McpServers };
    }

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Files_are_compared_per_entry()
    {
        var plan = ClaudePlanner.Plan(
            Source(files: [F("CLAUDE.md", "new"), F("skills/a/SKILL.md", "a"), F("skills/b/SKILL.md", "b"), F("commands/x.md", "x")]),
            Target(files: [F("CLAUDE.md", "old"), F("skills/a/SKILL.md", "a"), F("skills/c/SKILL.md", "c")]));

        var md = plan.Entries.Single(e => e.Item.Key == "claude-md");
        Assert.Equal((0, 1, 0), (md.Added, md.Changed, md.Same));
        var skills = plan.Entries.Single(e => e.Item.Key == "skills");
        Assert.Equal((1, 0, 1, 1), (skills.Added, skills.Changed, skills.Same, skills.OnlyOnTarget));
        Assert.Equal(["skills/b/SKILL.md"], skills.ToSend.Select(f => f.Path));
        Assert.False(plan.Entries.Single(e => e.Item.Key == "agents").HasChanges);
    }

    [Fact]
    public void Settings_keys_merge_and_commands_become_executable_items()
    {
        var plan = ClaudePlanner.Plan(
            Source(Json("""
                { "model": "opus", "effortLevel": "high", "env": { "API_TOKEN": "abc", "LANG": "de" },
                  "statusLine": { "type": "command", "command": "node /home/a/.claude/sl.js" },
                  "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "/opt/snip hook" } ] } ],
                             "Stop": [ { "hooks": [ { "type": "command", "command": "echo done" } ] } ] },
                  "enabledPlugins": { "a@m": true } }
                """)),
            Target(Json("""
                { "model": "opus", "effortLevel": "low", "env": { "API_TOKEN": "other", "LANG": "de" }, "theme": "dark",
                  "statusLine": { "type": "command", "command": "node C:\\Users\\B\\.claude\\sl.js" },
                  "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "echo done" } ] } ] } }
                """)));

        // env is the same once secrets are removed on both sides; the status line is the same once folders are variables.
        Assert.Equal(["effortLevel", "hooks"], plan.Settings.Select(s => s.Key));
        var hook = plan.Settings.Single(s => s.Key == "hooks");
        Assert.Equal(("PreToolUse", "hook:PreToolUse"), (hook.SubKey, hook.ExecutableId));
        var item = Assert.Single(plan.Executables);
        Assert.Equal("/opt/snip hook", item.Command);
        Assert.Equal(["/opt/snip"], item.UnmappedPaths);
        var entry = plan.Entries[0];
        Assert.Equal((0, 1, 2, 1), (entry.Added, entry.Changed, entry.Same, entry.OnlyOnTarget));
    }

    [Fact]
    public void Plugins_install_enable_disable_and_skip_synced()
    {
        var plan = ClaudePlanner.Plan(
            Source(plugins:
                [
                    new("new@m1", true, true, "1.2.0", false), new("off@m1", true, false, null, false), new("there@m1", true, true, null, false),
                    new("sleeping@m1", true, true, null, false), new("mine@synced", true, true, null, true), new("local@dir", true, true, null, false),
                ],
                marketplaces:
                [
                    new("m1", Json("""{ "source": "github", "repo": "org/m1", "ref": "v1.2.0" }"""), true),
                    new("dir", Json("""{ "source": "directory", "path": "/home/a/m" }"""), null),
                ]),
            Target(plugins: [new("off@m1", true, true, null, false), new("there@m1", true, true, null, false), new("sleeping@m1", true, false, null, false)]));

        Assert.Equal(
            [(ClaudeStepKind.MarketplaceAdd, "m1"), (ClaudeStepKind.PluginInstall, "new@m1"), (ClaudeStepKind.PluginDisable, "off@m1"), (ClaudeStepKind.PluginEnable, "sleeping@m1")],
            plan.Steps.Select(s => (s.Kind, s.Name)));
        Assert.Equal("org/m1#v1.2.0", plan.Steps.Single(s => s.Kind == ClaudeStepKind.MarketplaceAdd).Value);
        Assert.Equal("enabledPlugins · 1.2.0 · auto-update on", plan.Steps.Single(s => s.Kind == ClaudeStepKind.PluginInstall).Meta);
        Assert.Equal(["mine@synced"], plan.SyncedPlugins);
        Assert.Contains(plan.Notes, n => n.StartsWith("local@dir", StringComparison.Ordinal));
        Assert.Equal(["plugin:new@m1", "plugin:sleeping@m1"], plan.Executables.Select(e => e.Id));
    }

    [Fact]
    public void Mcp_servers_are_classified_redacted_and_compared()
    {
        var plan = ClaudePlanner.Plan(
            Source(mcp: new()
            {
                ["web"] = Json("""{ "type": "http", "url": "https://mcp.example.com", "headers": { "Authorization": "Bearer abc123" } }"""),
                ["fs"] = Json("""{ "command": "npx", "args": ["-y", "@mcp/fs", "/home/a/projects"] }"""),
                ["lt"] = Json("""{ "command": "/opt/lt/bin/lt", "env": { "LT_TOKEN": "xyz" } }"""),
            }),
            Target(mcp: new() { ["fs"] = Json("""{ "command": "npx", "args": ["-y", "@mcp/fs", "C:\\Users\\B\\projects"] }""") }));

        Assert.Equal(
            [("fs", McpKind.PackageRunner, true), ("lt", McpKind.AbsolutePath, false), ("web", McpKind.Http, false)],
            plan.McpServers.Select(m => (m.Name, m.Kind, m.Unchanged)));
        Assert.Equal(["WEB_AUTHORIZATION"], plan.McpServers.Single(m => m.Name == "web").SecretVariables);
        var lt = plan.Steps.Single(s => s.Name == "lt");
        Assert.Contains("${LT_TOKEN}", lt.Value);
        Assert.DoesNotContain("xyz", lt.Value);
        Assert.Null(plan.Steps.Single(s => s.Name == "web").ExecutableId);
        Assert.Equal(["/opt/lt/bin/lt"], plan.Executables.Single(e => e.Id == "mcp:lt").UnmappedPaths);
    }

    [Fact]
    public void Apply_request_keeps_ticked_and_confirmed_items_and_maps_paths()
    {
        var plan = ClaudePlanner.Plan(
            Source(Json("""{ "model": "opus", "statusLine": { "type": "command", "command": "/opt/sl" } }"""),
                files: [F("CLAUDE.md", "x"), F("skills/a.md", "a")],
                plugins: [new("p@m", true, true, null, false), new("q@m", true, true, null, false)],
                marketplaces: [new("m", Json("""{ "source": "github", "repo": "o/m" }"""), null)],
                mcp: new() { ["lt"] = Json("""{ "command": "/opt/lt" }"""), ["web"] = Json("""{ "url": "https://x" }""") }),
            Target());

        var none = ClaudePlanner.BuildApply(plan, new ClaudeSelection(new HashSet<string> { "skills" }, new HashSet<string>(), new Dictionary<string, string>()));
        Assert.Equal(["skills/a.md"], none.Files.Select(f => f.Path));
        Assert.Empty(none.Settings);
        Assert.Equal(["web"], none.Steps.Select(s => s.Name));

        var all = ClaudePlanner.BuildApply(plan, new ClaudeSelection(
            new HashSet<string> { "settings", "claude-md" }, new HashSet<string> { "setting:statusLine", "plugin:q@m", "mcp:lt" },
            new Dictionary<string, string> { ["/opt/sl"] = "${TOOLS}/sl", ["/opt/lt"] = "${TOOLS}/lt" }));
        Assert.Equal(["CLAUDE.md"], all.Files.Select(f => f.Path));
        Assert.Equal(["model", "statusLine"], all.Settings.Select(s => s.Key));
        Assert.Equal("${TOOLS}/sl", (string?)all.Settings[1].Value["command"]);
        Assert.Equal([(ClaudeStepKind.MarketplaceAdd, "m"), (ClaudeStepKind.PluginInstall, "q@m"), (ClaudeStepKind.McpAdd, "lt"), (ClaudeStepKind.McpAdd, "web")],
            all.Steps.Select(s => (s.Kind, s.Name)));
        Assert.Contains("${TOOLS}/lt", all.Steps.Single(s => s.Name == "lt").Value);
    }

    [Theory]
    [InlineData("""{ "source": "github", "repo": "org/m" }""", "org/m")]
    [InlineData("""{ "source": "git", "url": "https://x.dev/m.git", "ref": "main" }""", "https://x.dev/m.git#main")]
    [InlineData("""{ "source": "github", "repo": "--evil" }""", null)]
    [InlineData("""{ "source": "github", "repo": "a b" }""", null)]
    [InlineData("""{ "source": "directory", "path": "/x" }""", null)]
    public void Marketplace_arguments_are_safe(string source, string? expected) =>
        Assert.Equal(expected, ClaudePlanner.MarketplaceArgument(Json(source)));
    [Fact]
    public void Missing_claude_on_the_target_is_offered_as_first_confirmable_step()
    {
        var source = Source(plugins: [new ClaudePlugin("good@m", true, true, "1.0", false)],
            marketplaces: [new ClaudeMarketplace("m", Json("""{"source":"github","repo":"org/m"}"""), null)]);

        var plan = ClaudePlanner.Plan(source, Target(), offerInstall: true);

        Assert.Equal(ClaudeStepKind.InstallClaude, plan.Steps[0].Kind);
        Assert.Equal(ClaudePlanner.InstallClaudeId, plan.Executables[0].Id);
        Assert.Contains("install.ps1", plan.Executables[0].Command, StringComparison.Ordinal);

        var all = ClaudePlanner.BuildApply(plan, new ClaudeSelection(new HashSet<string>(),
            plan.Executables.Select(e => e.Id).ToHashSet(), new Dictionary<string, string>()));
        Assert.Equal([ClaudeStepKind.InstallClaude, ClaudeStepKind.MarketplaceAdd, ClaudeStepKind.PluginInstall], all.Steps.Select(s => s.Kind));
        var unconfirmed = ClaudePlanner.BuildApply(plan, new ClaudeSelection(new HashSet<string>(),
            new HashSet<string> { "plugin:good@m" }, new Dictionary<string, string>()));
        Assert.DoesNotContain(unconfirmed.Steps, s => s.Kind == ClaudeStepKind.InstallClaude);
    }

    [Fact]
    public void Install_is_not_offered_when_claude_is_there_or_the_target_is_too_old()
    {
        Assert.DoesNotContain(ClaudePlanner.Plan(Source(), Target() with { Version = "2.1.283" }, offerInstall: true).Steps,
            s => s.Kind == ClaudeStepKind.InstallClaude);
        Assert.DoesNotContain(ClaudePlanner.Plan(Source(), Target(), offerInstall: false).Steps, s => s.Kind == ClaudeStepKind.InstallClaude);
    }
    [Fact]
    public void Missing_tools_come_first_then_claude_then_the_cli_steps()
    {
        var source = Source(plugins: [new ClaudePlugin("good@m", true, true, "1.0", false)],
            marketplaces: [new ClaudeMarketplace("m", Json("""{"source":"github","repo":"org/m"}"""), null)]);

        var plan = ClaudePlanner.Plan(source, Target() with { MissingTools = ["git", "jq"] }, offerInstall: true);
        var request = ClaudePlanner.BuildApply(plan, new ClaudeSelection(new HashSet<string>(),
            plan.Executables.Select(e => e.Id).ToHashSet(), new Dictionary<string, string>()));

        Assert.Equal(["tool:git", "tool:jq", ClaudePlanner.InstallClaudeId], plan.Executables.Take(3).Select(e => e.Id));
        Assert.Contains("winget", plan.Executables[0].Command, StringComparison.Ordinal);
        Assert.Equal([ClaudeStepKind.InstallTool, ClaudeStepKind.InstallTool, ClaudeStepKind.InstallClaude, ClaudeStepKind.MarketplaceAdd,
            ClaudeStepKind.PluginInstall], request.Steps.Select(s => s.Kind));
        Assert.DoesNotContain(ClaudePlanner.Plan(source, Target()).Steps, s => s.Kind == ClaudeStepKind.InstallTool);
    }
}
