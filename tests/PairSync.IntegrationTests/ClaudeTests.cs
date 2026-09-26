using System.Text.Json.Nodes;
using PairSync.Application;
using PairSync.Application.Claude;
using PairSync.Protocol;
using PairSync.Storage;

namespace PairSync.IntegrationTests;

/// <summary>Two app cores with their own Claude Code folders and a fake <c>claude</c> CLI that logs its arguments.</summary>
public sealed class ClaudeTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-claude-");
    private readonly List<PairSyncCore> _cores = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var core in _cores)
            await core.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private string Home(string name) => Path.Combine(_root.FullName, name, "home");

    private string ConfigDir(string name) => Path.Combine(Home(name), ".claude");

    private string CliLog(string name) => Path.Combine(_root.FullName, name, "cli.log");

    /// <summary>A fake CLI: logs the arguments, answers --version, fails installing <c>bad@m</c>.</summary>
    private string FakeCli(string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, name, "bin")).FullName;
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(folder, "claude.bat");
            File.WriteAllText(path, $"""
                @echo off
                echo %*>>"{CliLog(name)}"
                if "%1"=="--version" echo 2.1.283 (Claude Code)
                if "%3"=="bad@m" exit /b 1
                exit /b 0
                """.ReplaceLineEndings("\r\n"));
            return path;
        }
        var script = Path.Combine(folder, "claude");
        File.WriteAllText(script, $"""
            #!/bin/sh
            echo "$*" >> "{CliLog(name)}"
            [ "$1" = "--version" ] && echo "2.1.283 (Claude Code)"
            [ "$3" = "bad@m" ] && exit 1
            exit 0
            """.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        return script;
    }

    private async Task<PairSyncCore> StartAsync(string name, bool withCli = true)
    {
        var options = new ClaudeOptions
        {
            HomeDir = Home(name),
            ConfigDir = ConfigDir(name),
            GlobalConfigFile = Path.Combine(Home(name), ".claude.json"),
            Executable = withCli ? FakeCli(name) : "",
            StepTimeout = TimeSpan.FromSeconds(30),
        };
        Directory.CreateDirectory(options.ConfigDir);
        var core = await TestCores.StartAsync(new DataDirectory(Path.Combine(_root.FullName, name, "data")), Ct, claude: options);
        core.Settings.Update(s => s with { DeviceName = name });
        _cores.Add(core);
        return core;
    }

    private async Task<(PairSyncCore Source, PairSyncCore Target)> StartPairAsync(bool targetCli = true)
    {
        var source = await StartAsync("laptop");
        var target = await StartAsync("office", targetCli);
        await TestCores.PairAsync(source, target, Ct);
        TestCores.MakeReachable(source, target);
        TestCores.MakeReachable(target, source);
        return (source, target);
    }

    private static void Write(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task AllowAsync(PairSyncCore target, PairSyncCore source, bool programs)
    {
        var device = (await target.Devices.GetPairedAsync(Ct)).Single();
        await target.Devices.SetClaudePermissionsAsync(device.Id, true, programs, Ct);
    }

    private static async Task<Application.Claude.ClaudeTarget> FetchAsync(PairSyncCore source) =>
        await source.Claude.FetchTargetAsync((await source.Devices.GetPairedAsync(Ct)).Single(), Ct);

    private void WriteSourceConfig()
    {
        var config = ConfigDir("laptop");
        Write(config, "CLAUDE.md", "new rules");
        Write(config, "skills/review/SKILL.md", "review skill");
        Write(config, "settings.json", $$"""
            {
              "model": "opus",
              "env": { "API_TOKEN": "sk-live-secret-value", "LANG": "de" },
              "statusLine": { "type": "command", "command": "node {{config.Replace("\\", "/")}}/statusline.js" },
              "enabledPlugins": { "good@m": true, "bad@m": true, "mine@synced": true }
            }
            """);
        Write(config, "plugins/known_marketplaces.json", """{ "m": { "source": { "source": "github", "repo": "org/m" } } }""");
        Write(config, ".credentials.json", """{ "token": "never" }""");
        Write(Home("laptop"), ".claude.json", """{ "userID": "x", "mcpServers": { "lt": { "command": "npx", "args": ["-y", "lt-mcp"], "env": { "LT_TOKEN": "abc123secretvalue" } } } }""");
    }

    [Fact]
    public async Task Target_without_permission_refuses_and_reveals_nothing()
    {
        var (source, _) = await StartPairAsync();

        var e = await Assert.ThrowsAsync<ClaudeUnavailableException>(() => FetchAsync(source));

        Assert.True(e.NotAllowed);
    }

    [Fact]
    public async Task Apply_writes_files_and_settings_with_backup_and_runs_confirmed_steps()
    {
        var (source, target) = await StartPairAsync();
        await AllowAsync(target, source, programs: true);
        WriteSourceConfig();
        Write(ConfigDir("office"), "CLAUDE.md", "old rules");
        Write(ConfigDir("office"), "settings.json", """{ "theme": "dark", "env": { "API_TOKEN": "office-token-1234567890abcdef" } }""");

        var fetched = await FetchAsync(source);
        Assert.Equal("2.1.283", fetched.Snapshot.Version);
        Assert.True(fetched.AllowPrograms);
        var plan = ClaudePlanner.Plan(await source.Claude.ReadLocalAsync(Ct), fetched.Snapshot);
        Assert.Equal(["mine@synced"], plan.SyncedPlugins);
        var request = ClaudePlanner.BuildApply(plan, new ClaudeSelection(
            PortableItem.All.Select(i => i.Key).ToHashSet(), plan.Executables.Select(e => e.Id).ToHashSet(), new Dictionary<string, string>()));
        var results = new List<ClaudeStepResult>();

        var outcome = await source.Claude.ApplyAsync(fetched.Device, request, results.Add, Ct);

        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.FilesWritten);
        var office = ConfigDir("office");
        Assert.Equal("new rules", File.ReadAllText(Path.Combine(office, "CLAUDE.md")));
        Assert.Equal("review skill", File.ReadAllText(Path.Combine(office, "skills/review/SKILL.md")));
        Assert.False(File.Exists(Path.Combine(office, ".credentials.json")));
        Assert.Equal("old rules", File.ReadAllText(Path.Combine(outcome.BackupFolder!, "CLAUDE.md")));

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(office, "settings.json")))!;
        Assert.Equal("dark", (string?)settings["theme"]);
        Assert.Equal("opus", (string?)settings["model"]);
        Assert.Equal("office-token-1234567890abcdef", (string?)settings["env"]!["API_TOKEN"]);
        Assert.Equal("de", (string?)settings["env"]!["LANG"]);
        Assert.Equal($"node {office.Replace('\\', '/')}/statusline.js", (string?)settings["statusLine"]!["command"]);
        Assert.Null(settings["enabledPlugins"]);

        Assert.Equal([ClaudeStepStatus.Done, ClaudeStepStatus.Failed, ClaudeStepStatus.Done, ClaudeStepStatus.Done], results.Select(r => r.Status));
        var log = File.ReadAllText(CliLog("office"));
        Assert.Contains("plugin marketplace add org/m", log);
        Assert.Contains("plugin install good@m --scope user --yes", log);
        Assert.Contains("mcp add-json --scope user lt", log);
        Assert.Contains("${LT_TOKEN}", log);
        Assert.DoesNotContain("abc123secretvalue", log);
        Assert.DoesNotContain("synced", log);
    }

    [Fact]
    public async Task Without_install_permission_commands_are_left_out()
    {
        var (source, target) = await StartPairAsync();
        await AllowAsync(target, source, programs: false);
        WriteSourceConfig();

        var fetched = await FetchAsync(source);
        Assert.False(fetched.AllowPrograms);
        var plan = ClaudePlanner.Plan(await source.Claude.ReadLocalAsync(Ct), fetched.Snapshot);
        // A source that ignores the target's answer and confirms everything anyway.
        var request = ClaudePlanner.BuildApply(plan, new ClaudeSelection(
            PortableItem.All.Select(i => i.Key).ToHashSet(), plan.Executables.Select(e => e.Id).ToHashSet(), new Dictionary<string, string>()));
        var results = new List<ClaudeStepResult>();

        var outcome = await source.Claude.ApplyAsync(fetched.Device, request, results.Add, Ct);

        Assert.Null(outcome.Error);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(ConfigDir("office"), "settings.json")))!;
        Assert.Equal("opus", (string?)settings["model"]);
        Assert.Null(settings["statusLine"]);
        Assert.Contains(outcome.Notes, n => n.StartsWith("statusLine left out", StringComparison.Ordinal));
        Assert.All(results, r => Assert.Equal(ClaudeStepStatus.Skipped, r.Status));
        Assert.False(File.Exists(CliLog("office")) && File.ReadAllText(CliLog("office")).Contains("plugin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Steps_wait_on_a_target_without_claude_and_run_later()
    {
        var (source, target) = await StartPairAsync(targetCli: false);
        await AllowAsync(target, source, programs: true);
        WriteSourceConfig();

        var fetched = await FetchAsync(source);
        Assert.Null(fetched.Snapshot.Version);
        var plan = ClaudePlanner.Plan(await source.Claude.ReadLocalAsync(Ct), fetched.Snapshot);
        var request = ClaudePlanner.BuildApply(plan, new ClaudeSelection(
            new HashSet<string>(), new HashSet<string> { "plugin:good@m" }, new Dictionary<string, string>()));
        var results = new List<ClaudeStepResult>();
        await source.Claude.ApplyAsync(fetched.Device, request, results.Add, Ct);

        Assert.All(results, r => Assert.Equal(ClaudeStepStatus.Pending, r.Status));
        var pending = target.Claude.Pending;
        Assert.NotNull(pending);
        Assert.Equal("laptop", pending.DeviceName);
        Assert.Equal([ClaudeStepKind.MarketplaceAdd, ClaudeStepKind.PluginInstall], pending.Steps.Select(s => s.Kind));
        await Assert.ThrowsAsync<ClaudeUnavailableException>(() => target.Claude.ApplyPendingAsync(_ => { }, Ct));

        ((ClaudeOptions)target.Services.GetService(typeof(ClaudeOptions))!).Executable = FakeCli("office");
        var later = await target.Claude.ApplyPendingAsync(_ => { }, Ct);

        Assert.All(later, r => Assert.Equal(ClaudeStepStatus.Done, r.Status));
        Assert.Contains("plugin install good@m", File.ReadAllText(CliLog("office")));
        Assert.Null(target.Claude.Pending);
    }

    [Fact]
    public async Task Paths_outside_the_portable_entries_are_rejected_before_anything_is_written()
    {
        var (source, target) = await StartPairAsync();
        await AllowAsync(target, source, programs: true);
        var sha = System.Security.Cryptography.SHA256.HashData("x"u8);

        foreach (var path in new[] { "../evil.txt", ".credentials.json", "skills/../../evil.txt", "settings.local.json", "C:/evil.txt", @"skills/a\..\..\evil.txt" })
        {
            // A source that does not use the planner: straight to the wire.
            await using var connection = await TestCores.ConnectAsync(source, target, Ct);
            await connection.Channels.Control.SendAsync(new ClaudeApply { Files = [new ClaudeFileEntry { Path = path, Size = 1, Sha256 = sha }] }, Ct);
            await connection.Channels.Control.SendAsync(new ClaudeFileData { Path = path, Data = "x"u8.ToArray(), Last = true }, Ct);
            ClaudeApplyDone? done = null;
            await foreach (var message in connection.Channels.Control.ReadAllAsync(Ct))
            {
                if ((done = message as ClaudeApplyDone) is not null)
                    break;
            }
            Assert.NotNull(done?.Error);
        }
        Assert.False(File.Exists(Path.Combine(Home("office"), "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_root.FullName, "office", "evil.txt")));
        Assert.False(File.Exists(Path.Combine(ConfigDir("office"), ".credentials.json")));
    }

    [Fact]
    public async Task Settings_that_manage_plugins_are_rejected()
    {
        var (source, target) = await StartPairAsync();
        await AllowAsync(target, source, programs: true);
        await using var connection = await TestCores.ConnectAsync(source, target, Ct);

        await connection.Channels.Control.SendAsync(new ClaudeApply
        {
            Settings = [new ClaudeSettingEntry { Key = "enabledPlugins", Json = """{ "evil@x": true }""" }],
        }, Ct);

        await foreach (var message in connection.Channels.Control.ReadAllAsync(Ct))
        {
            if (message is ClaudeApplyDone done)
            {
                Assert.NotNull(done.Error);
                break;
            }
        }
        Assert.False(File.Exists(Path.Combine(ConfigDir("office"), "settings.json")));
    }
}
