using PairSync.Desktop.ViewModels;

namespace PairSync.UiTests;

/// <summary>The Claude Code screen (phase 4 block E): compare with a paired device, confirm, apply.</summary>
public sealed class ClaudeScreenTests(HeadlessFixture ui)
{
    private static string ConfigDir(TestApp app) => app.Core.Claude.Environment.ConfigDir;

    private static void Write(string folder, string path, string content)
    {
        var full = Path.Combine(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static async Task<(TestApp Laptop, TestApp Office)> StartAsync(TwoCores cores)
    {
        var laptop = await cores.StartAsync("laptop-win11");
        var office = await cores.StartAsync("office-pc");
        await TwoCores.PairAsync(laptop, office);
        TwoCores.MakeReachable(laptop, office);
        TwoCores.MakeReachable(office, laptop);
        return (laptop, office);
    }

    [Fact]
    public Task Plan_is_shown_and_applied_on_the_other_device() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var (laptop, office) = await StartAsync(cores);
        await office.Core.Devices.SetClaudePermissionsAsync(laptop.Id, true, false, CancellationToken.None);
        Write(ConfigDir(laptop), "CLAUDE.md", "rules");
        Write(ConfigDir(laptop), "skills/review/SKILL.md", "skill");
        Write(ConfigDir(laptop), "settings.json", """{ "model": "opus", "statusLine": { "type": "command", "command": "/opt/tools/statusline" } }""");

        var page = laptop.Page<ClaudeCodeViewModel>();
        laptop.Shell.Navigate(AppPage.ClaudeCode);
        await UiAsync.UntilAsync(() => page.IsLoaded);

        Assert.Equal("Claude Code → office-pc", page.Heading);
        Assert.Contains("Claude Code not installed there", page.Subline, StringComparison.Ordinal);
        Assert.Equal("Apply on office-pc", page.ApplyLabel);
        Assert.Equal("+1", page.PortableItems.Single(p => p.Key == "claude-md").Status);
        Assert.Equal("1 key", page.PortableItems.Single(p => p.Key == "settings").Status);
        Assert.False(page.PortableItems.Single(p => p.Key == "agent-memory").Enabled);
        var install = page.Executables.Single(e => e.Item.Id == PairSync.Application.Claude.ClaudePlanner.InstallClaudeId);
        Assert.Equal(PairSync.Application.Claude.ClaudePlanner.InstallClaudeId, install.Item.Id);
        Assert.False(install.Allowed);
        Assert.True(page.LocalMissing);
        Assert.True(page.ToolsMissing);
        Assert.Equal(["tool:git", "tool:bun", "tool:jq"], page.Executables.Take(3).Select(e => e.Item.Id));
        var statusLine = page.Executables.Single(e => e.Item.Kind != PairSync.Application.Claude.ExecutableKind.Installer);
        Assert.False(statusLine.Confirmed);
        Assert.False(statusLine.Allowed);
        Assert.Equal("office-pc does not allow this device to install programs. Allow it there under Devices → Permissions.", page.ProgramsHint);
        Assert.Equal("/opt/tools/statusline", page.PathMappings.Single().Original);
        Assert.Equal("0 of 5 confirmed. Unchecked items are left out; everything else still applies.", page.ConfirmedText);
        Snapshots.Save(laptop.Window, "screen-claude");

        await page.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("rules", File.ReadAllText(Path.Combine(ConfigDir(office), "CLAUDE.md")));
        Assert.Equal("skill", File.ReadAllText(Path.Combine(ConfigDir(office), "skills", "review", "SKILL.md")));
        Assert.DoesNotContain("statusLine", File.ReadAllText(Path.Combine(ConfigDir(office), "settings.json")), StringComparison.Ordinal);
        Assert.StartsWith("Done: 2 files and 1 settings written.", page.ResultText, StringComparison.Ordinal);
        Assert.Equal("Apply on office-pc", page.Log.Heading);
        Assert.Contains(page.Log.Entries, e => e.Kind == ClaudeLogKind.Done);
        await page.Log.CopyCommand.ExecuteAsync(null);
        Assert.Contains("Done: 2 files and 1 settings written.", laptop.Desktop.Copied, StringComparison.Ordinal);

        // The target shows what the other device did there.
        var there = office.Page<ClaudeCodeViewModel>();
        await UiAsync.UntilAsync(() => there.Log.Entries.Any(e => e.Kind == ClaudeLogKind.Done));
        Assert.Equal("From laptop-win11", there.Log.Heading);
        Assert.False(there.IsReceiving);
        Assert.StartsWith("laptop-win11 applies its Claude Code configuration", there.Log.Entries[0].Title, StringComparison.Ordinal);
        Assert.Equal(AppPage.ClaudeCode, office.Shell.ActivePage.Page);
        Snapshots.Save(office.Window, "screen-claude-target");
        Assert.Equal("same", page.PortableItems.Single(p => p.Key == "claude-md").Status);
    });

    [Fact]
    public Task Without_permission_the_page_says_how_to_allow_it() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var (laptop, _) = await StartAsync(cores);

        var page = laptop.Page<ClaudeCodeViewModel>();
        laptop.Shell.Navigate(AppPage.ClaudeCode);
        await UiAsync.UntilAsync(() => page.Error is not null);

        Assert.Equal("office-pc does not allow this device to apply Claude Code configuration. Allow it there under Devices → Permissions.", page.Error);
        Assert.False(page.IsLoaded);
    });
}
