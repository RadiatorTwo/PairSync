using PairSync.Desktop.ViewModels;

namespace PairSync.UiTests;

/// <summary>Identity backup in Settings (phase 6 block B): export on one installation, import on another.</summary>
public sealed class IdentityScreenTests(HeadlessFixture ui)
{
    [Fact]
    public Task Exported_identity_replaces_the_identity_of_another_installation() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var old = await cores.StartAsync("old-pc");
        var fresh = await cores.StartAsync("new-pc");
        var backup = Path.Combine(cores.Root, "old-pc.pairsync-identity");

        old.Desktop.SaveFileToPick = backup;
        var exporting = old.Page<SettingsViewModel>();
        Assert.True(exporting.CanBackUpIdentity);
        _ = exporting.ExportIdentityCommand.ExecuteAsync(null);
        var password = Assert.IsType<IdentityPasswordDialogViewModel>(old.Shell.Dialogs.Current);
        password.Password = "correct horse";
        password.Repeat = "correct hors";
        password.ConfirmCommand.Execute(null);
        Assert.Equal("The passwords do not match.", password.Error);
        password.Repeat = "correct horse";
        password.ConfirmCommand.Execute(null);
        await UiAsync.UntilAsync(() => File.Exists(backup) && exporting.IdentityMessage is not null);

        fresh.Desktop.OpenFileToPick = backup;
        var importing = fresh.Page<SettingsViewModel>();
        _ = importing.ImportIdentityCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => fresh.Shell.Dialogs.Current is IdentityPasswordDialogViewModel);
        var unlock = (IdentityPasswordDialogViewModel)fresh.Shell.Dialogs.Current!;
        unlock.Password = "wrong password";
        unlock.ConfirmCommand.Execute(null);
        Assert.Contains("password", unlock.Error, StringComparison.OrdinalIgnoreCase);
        unlock.Password = "correct horse";
        unlock.ConfirmCommand.Execute(null);

        await UiAsync.UntilAsync(() => fresh.Shell.Dialogs.Current is ConfirmDialogViewModel);
        var replace = (ConfirmDialogViewModel)fresh.Shell.Dialogs.Current!;
        Assert.Contains(old.Id.ToString(), replace.Text, StringComparison.Ordinal);
        replace.ConfirmCommand.Execute(null);

        await UiAsync.UntilAsync(() => fresh.Shell.Dialogs.Current is ConfirmDialogViewModel { Title: "Quit PairSync to finish" });
        ((ConfirmDialogViewModel)fresh.Shell.Dialogs.Current!).CancelCommand.Execute(null);
        var record = await File.ReadAllTextAsync(Path.Combine(fresh.Core.DataDirectory.Root, "identity.json"));
        Assert.Contains(old.Id.ToString(), record, StringComparison.Ordinal);
    });
}
