using System.Text.Json.Nodes;
using PairSync.Application.Claude;

namespace PairSync.UnitTests.Claude;

public sealed class SecretDetectorTests
{
    [Theory]
    [InlineData("GITHUB_TOKEN", "abc")]
    [InlineData("OPENAI_API_KEY", "x")]
    [InlineData("DB_PASSWORD", "hunter2")]
    [InlineData("PLAIN", "sk-ant-1234567890")]
    [InlineData("PLAIN", "ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("PLAIN", "a8f5f167f44f4964e6c998dee827110c3e4a1b2c")]
    public void Recognizes_secrets(string name, string value) => Assert.True(SecretDetector.IsSecret(name, value));

    [Theory]
    [InlineData("NODE_ENV", "production")]
    [InlineData("GITHUB_TOKEN", "${GITHUB_TOKEN}")]
    [InlineData("GITHUB_TOKEN", "")]
    [InlineData("PATH_EXTRA", "/home/user/projects/some-very-long-folder-name-2026/bin")]
    [InlineData("MODEL", "claude-opus-5-5")]
    public void Leaves_ordinary_values(string name, string value) => Assert.False(SecretDetector.IsSecret(name, value));

    [Fact]
    public void Settings_env_secrets_become_the_placeholder()
    {
        var settings = new JsonObject { ["env"] = new JsonObject { ["API_TOKEN"] = "abc", ["LANG"] = "de" }, ["model"] = "opus" };
        var redacted = SecretDetector.RedactSettings(settings);
        Assert.Equal(SecretDetector.Placeholder, (string?)redacted["env"]!["API_TOKEN"]);
        Assert.Equal("de", (string?)redacted["env"]!["LANG"]);
        Assert.Equal("abc", (string?)settings["env"]!["API_TOKEN"]);
    }

    [Fact]
    public void Mcp_secrets_become_variable_references()
    {
        var server = JsonNode.Parse("""
            {"command":"npx","args":["-y","server","--api-key","sk-live-123456","--token=abcdef"],
             "env":{"GITHUB_TOKEN":"ghp_x","DEBUG":"1"},"headers":{"Authorization":"Bearer abc123"}}
            """)!.AsObject();
        var variables = new List<string>();
        var redacted = SecretDetector.RedactMcpServer("gh", server, variables);

        Assert.Equal("${GITHUB_TOKEN}", (string?)redacted["env"]!["GITHUB_TOKEN"]);
        Assert.Equal("1", (string?)redacted["env"]!["DEBUG"]);
        Assert.Equal("${GH_API_KEY}", (string?)redacted["args"]![3]);
        Assert.Equal("--token=${GH_TOKEN}", (string?)redacted["args"]![4]);
        Assert.Equal("server", (string?)redacted["args"]![1]);
        Assert.Equal("Bearer ${GH_AUTHORIZATION}", (string?)redacted["headers"]!["Authorization"]);
        Assert.Equal(["GITHUB_TOKEN", "GH_API_KEY", "GH_TOKEN", "GH_AUTHORIZATION"], variables);
    }
}

public sealed class ClaudeInventoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pairsync-claude-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Config => Path.Combine(_root, ".claude");

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Config, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Reads_only_portable_files_plugins_marketplaces_and_mcp_servers()
    {
        Write("settings.json", """
            { "enabledPlugins": { "a@m1": true, "b@m1": false, "c@synced": true, "--bad@x": true },
              "extraKnownMarketplaces": { "m2": { "source": { "source": "git", "url": "https://example.com/m2.git" } } } }
            """);
        Write("CLAUDE.md", "hi");
        Write("skills/one/SKILL.md", "skill");
        Write("skills/one/node_modules/x.js", "ignored");
        Write("commands/c.md", "cmd");
        Write("agent-memory/a.md", "memory");
        Write(".credentials.json", "{}");
        Write("settings.local.json", "{}");
        Write("history.jsonl", "");
        Write("projects/p/transcript.jsonl", "");
        Write("plugins/installed_plugins.json", """
            { "version": 2, "plugins": { "a@m1": [ { "scope": "user", "version": "1.2.0" } ], "d@m1": [ { "scope": "project", "version": "1" } ] } }
            """);
        Write("plugins/known_marketplaces.json", """{ "m1": { "source": { "source": "github", "repo": "org/m1" }, "autoUpdate": true } }""");
        File.WriteAllText(Path.Combine(_root, ".claude.json"), """
            { "userID": "secret-id", "mcpServers": { "fs": { "command": "npx", "args": ["x"] } }, "projects": { "p": { "mcpServers": { "local": {} } } } }
            """);

        var snapshot = ClaudeInventory.Read(new ClaudeEnvironment(Config, false, Path.Combine(_root, ".claude.json"), _root), "2.1.283");

        Assert.Equal(["CLAUDE.md", "skills/one/SKILL.md", "commands/c.md", "agent-memory/a.md"], snapshot.Files.Select(f => f.Path));
        Assert.Equal(["a@m1", "b@m1", "c@synced"], snapshot.Plugins.Select(p => p.Id));
        var a = snapshot.Plugins[0];
        Assert.True(a is { Installed: true, Enabled: true, Version: "1.2.0", Synced: false });
        Assert.True(snapshot.Plugins[2].Synced);
        Assert.Equal(["m1", "m2"], snapshot.Marketplaces.Select(m => m.Name));
        Assert.True(snapshot.Marketplaces[0].AutoUpdate);
        Assert.Equal(["fs"], snapshot.McpServers.Keys);
    }

    [Fact]
    public void Portable_items_map_paths_to_entries()
    {
        Assert.Equal("rules", PortableItem.For("commands/x.md")!.Key);
        Assert.Equal("themes", PortableItem.For("keybindings.json")!.Key);
        Assert.Null(PortableItem.For("settings.json"));
        Assert.Null(PortableItem.For("settings.local.json"));
        Assert.Null(PortableItem.For("skillsx/a"));
    }
}
