using PairSync.Application.Sync;
using PairSync.Desktop.ViewModels;

namespace PairSync.UiTests;

/// <summary>Sync profiles on the screens (phase 3 block F): new profile, incoming offer, table, conflict dialog.</summary>
public sealed class SyncScreenTests(HeadlessFixture ui)
{
    private static string Folder(TwoCores cores, string name) => Directory.CreateDirectory(Path.Combine(cores.Root, "folders", name)).FullName;

    private static void Write(string folder, string path, string content)
    {
        var full = Path.Combine(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(-10));
    }

    /// <summary>laptop offers <paramref name="laptopFolder"/> through "New profile", office accepts it for <paramref name="officeFolder"/>.</summary>
    private static async Task ShareAsync(TestApp laptop, TestApp office, string laptopFolder, string officeFolder)
    {
        var syncs = laptop.Page<SyncsViewModel>();
        laptop.Shell.Navigate(AppPage.Syncs);
        _ = syncs.NewProfileCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => laptop.Shell.Dialogs.Current is NewProfileDialogViewModel);
        var create = (NewProfileDialogViewModel)laptop.Shell.Dialogs.Current!;
        Assert.False(create.CreateCommand.CanExecute(null));
        laptop.Desktop.FolderToPick = laptopFolder;
        await create.BrowseCommand.ExecuteAsync(null);
        Assert.Equal(Path.GetFileName(laptopFolder), create.Name);
        Assert.Equal("office-pc", create.Device!.Label);
        Snapshots.Save(laptop.Window, "screen-syncs-new");
        await create.CreateCommand.ExecuteAsync(null);
        Assert.Null(create.Error);

        // The offer opens its dialog on the other device.
        await UiAsync.UntilAsync(() => office.Shell.Dialogs.Current is IncomingOfferDialogViewModel);
        var offer = (IncomingOfferDialogViewModel)office.Shell.Dialogs.Current!;
        Assert.Equal($"laptop-win11 wants to sync “{Path.GetFileName(laptopFolder)}”", offer.Title);
        Assert.Equal(AppPage.Syncs, office.Shell.ActivePage.Page);
        offer.LocalPath = officeFolder;
        Snapshots.Save(office.Window, "screen-syncs-offer");
        await offer.AcceptCommand.ExecuteAsync(null);
        Assert.Null(offer.Error);
    }

    [Fact]
    public Task Profile_is_offered_accepted_and_synced_on_both_screens() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var office = await cores.StartAsync("office-pc");
        await TwoCores.PairAsync(laptop, office);
        TwoCores.MakeReachable(laptop, office);
        TwoCores.MakeReachable(office, laptop);
        var (laptopFolder, officeFolder) = (Folder(cores, "Projects"), Folder(cores, "office-projects"));
        Write(laptopFolder, "docs/plan.md", "plan");

        await ShareAsync(laptop, office, laptopFolder, officeFolder);

        await UiAsync.UntilAsync(() => File.Exists(Path.Combine(officeFolder, "docs", "plan.md")));
        var syncs = laptop.Page<SyncsViewModel>();
        await UiAsync.UntilAsync(() => syncs.Profiles.Count == 1 && syncs.Profiles[0].Status == "Up to date");
        var row = syncs.Profiles.Single();
        Assert.Equal("Projects", row.Name);
        Assert.Equal("office-pc", row.Device);
        Assert.Equal("Two-way", row.Direction);
        Assert.Equal("office-pc may", syncs.RightsTitle);
        Snapshots.Save(laptop.Window, "screen-syncs");

        // Settings of the selected profile are saved and survive a refresh.
        syncs.SelectedMode = syncs.Modes.Single(m => m.Value == PairSync.Domain.SyncMode.Manual);
        await UiAsync.UntilAsync(() => laptop.Core.Sync.GetProfilesAsync(CancellationToken.None).Result.Single().Profile.Mode == PairSync.Domain.SyncMode.Manual);
        syncs.AllowDelete = false;
        await UiAsync.UntilAsync(() => !laptop.Core.Sync.GetProfilesAsync(CancellationToken.None).Result.Single().Profile.AllowDelete);

        await syncs.PauseAllCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => syncs.Profiles.Single().Status == "Paused" && syncs.PauseAllLabel == "Resume all");
    });

    [Fact]
    public Task Conflict_shows_in_the_list_and_the_dialog_resolves_it() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var office = await cores.StartAsync("office-pc");
        await TwoCores.PairAsync(laptop, office);
        TwoCores.MakeReachable(laptop, office);
        TwoCores.MakeReachable(office, laptop);
        var (laptopFolder, officeFolder) = (Folder(cores, "Projects"), Folder(cores, "office-projects"));
        Write(laptopFolder, "plan.md", "laptop version");
        Write(officeFolder, "plan.md", "office version");

        await ShareAsync(laptop, office, laptopFolder, officeFolder);

        var syncs = laptop.Page<SyncsViewModel>();
        await UiAsync.UntilAsync(() => syncs.Conflicts.Count == 1 && syncs.Profiles.Single().Status == "1 conflict");
        Assert.Equal("plan.md", syncs.Conflicts.Single().Path);
        _ = syncs.Conflicts.Single().ResolveCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => laptop.Shell.Dialogs.Current is ConflictDialogViewModel);
        var dialog = (ConflictDialogViewModel)laptop.Shell.Dialogs.Current!;
        Assert.Equal("Conflict · Projects", dialog.Rubric);
        Assert.Equal("plan.md was changed on both devices", dialog.Title);
        Assert.Equal("Keep office-pc's", dialog.KeepOtherLabel);
        Assert.Equal("This device", dialog.Local.Device);
        Assert.StartsWith("SHA-256 ", dialog.Local.Hash, StringComparison.Ordinal);
        Snapshots.Save(laptop.Window, "screen-syncs-conflict");

        await dialog.KeepBothCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => syncs.Conflicts.Count == 0);
        await UiAsync.UntilAsync(() => Directory.GetFiles(laptopFolder, "plan (conflict *").Length == 1);
    });
}
