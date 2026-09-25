using Avalonia.Controls;
using Avalonia.VisualTree;
using PairSync.Application.Presence;
using PairSync.Desktop.ViewModels;
using PairSync.Domain;

namespace PairSync.UiTests;

/// <summary>The screens against two real app cores on loopback: send, confirm, pair, manage devices.</summary>
public sealed class ScreenTests(HeadlessFixture ui)
{
    [Fact]
    public Task Send_screen_sends_and_overview_shows_the_result() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var workstation = await cores.StartAsync("workstation");
        await TwoCores.PairAsync(laptop, workstation);
        TwoCores.MakeReachable(laptop, workstation);
        var file = cores.SourceFile("Photos-2026.tar", 300_000);

        laptop.Shell.Navigate(AppPage.Send);
        var send = laptop.Page<SendViewModel>();
        await UiAsync.UntilAsync(() => send.Target is { IsOnline: true });
        Assert.Equal("workstation", send.Target!.Name);
        laptop.Desktop.FilesToPick.Add(file);
        await send.BrowseFilesCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => send.SendCommand.CanExecute(null));

        Assert.Equal("1 item · 1 file", send.ItemsSummary);
        Assert.Equal("300 KB", send.SizeSummary);
        Assert.Equal("Send to workstation", send.SendLabel);
        Assert.Equal("Photos-2026.tar", send.Entries.Single().Display);
        Snapshots.Save(laptop.Window, "screen-send");

        // The receiver sees the offer as a dialog and accepts it.
        await send.SendCommand.ExecuteAsync(null);
        Assert.Equal(AppPage.Overview, laptop.Shell.ActivePage.Page);
        await UiAsync.UntilAsync(() => workstation.Shell.Dialogs.Current is IncomingTransferViewModel);
        var offer = (IncomingTransferViewModel)workstation.Shell.Dialogs.Current!;
        Assert.Equal("laptop-win11 wants to send you 1 item", offer.Title);
        Assert.Equal(1, workstation.Desktop.Reveals);
        Snapshots.Save(workstation.Window, "screen-incoming");
        await offer.AcceptCommand.ExecuteAsync(null);

        var overview = laptop.Page<OverviewViewModel>();
        await UiAsync.UntilAsync(() => overview.RecentTransfers.Count == 1);
        var recent = overview.RecentTransfers.Single();
        Assert.Equal("Photos-2026.tar", recent.Title);
        Assert.Equal("Done", recent.Outcome);
        Assert.StartsWith("→ workstation · 1 file · 300 KB", recent.Details, StringComparison.Ordinal);
        Assert.Empty(overview.ActiveTransfers);
        var card = overview.Devices.Single();
        Assert.Equal(CardKind.Connected, card.Kind);
        Assert.StartsWith("LAN · direct", card.Line1, StringComparison.Ordinal);
        Assert.Equal("1 paired device", overview.Header);
        Snapshots.Save(laptop.Window, "screen-overview");

        var received = Path.Combine(cores.Root, "workstation", "downloads", "PairSync", "laptop-win11", "Photos-2026.tar");
        await UiAsync.UntilAsync(() => File.Exists(received));
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(received));
    });

    [Fact]
    public Task Offline_device_is_shown_and_its_job_waits() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var nas = await cores.StartAsync("nas-box");
        await TwoCores.PairAsync(laptop, nas);

        var send = laptop.Page<SendViewModel>();
        await UiAsync.UntilAsync(() => send.Targets.Count == 1);
        Assert.False(send.Targets.Single().IsOnline);
        Assert.Equal("nas-box · offline", send.Targets.Single().ToString());
        Assert.Null(send.Target);

        await laptop.Core.Transfers.SendAsync(nas.Id,
            Application.Transfers.SendScanner.Scan([cores.SourceFile("notes.md", 10)]), ExistingFilePolicy.KeepBoth, null, CancellationToken.None);

        var overview = laptop.Page<OverviewViewModel>();
        await UiAsync.UntilAsync(() => overview.ActiveTransfers.Count == 1 && overview.Devices.Count == 1);
        var card = overview.Devices.Single();
        Assert.Equal(CardKind.Offline, card.Kind);
        Assert.Equal("Offline", card.State);
        Assert.Equal("1 job waiting", card.Line2);
        var row = overview.ActiveTransfers.Single();
        Assert.Equal("notes.md", row.Title);
        Assert.Equal("→ nas-box", row.Peer);
        Assert.Equal("Waiting for nas-box to come online", row.Meta);
        Assert.True(row.CanPause);

        await row.PauseCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => row.State == JobState.Paused);
        Assert.True(row.CanResume);
        Assert.Equal("Paused", row.Meta);
    });

    [Fact]
    public Task Pairing_by_invitation_on_both_screens() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var workstation = await cores.StartAsync("workstation");
        var inviting = workstation.Page<DevicesViewModel>().Pairing;
        var joining = laptop.Page<DevicesViewModel>().Pairing;

        inviting.CreateInvitationCommand.Execute(null);
        Assert.Equal(PairingStep.Invitation, inviting.Step);
        Assert.NotNull(inviting.QrCode);
        Assert.StartsWith("Expires in 0", inviting.ExpiresText, StringComparison.Ordinal);
        Assert.Equal("Step 2 of 3", inviting.StepText);
        await inviting.CopyInvitationCommand.ExecuteAsync(null);
        workstation.Shell.Navigate(AppPage.Devices);
        Snapshots.Save(workstation.Window, "screen-devices-invitation");

        joining.InvitationInput = workstation.Desktop.Copied!;
        await joining.ImportCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => inviting.Step == PairingStep.Code && joining.Step == PairingStep.Code);
        Assert.Equal(inviting.SecurityCode, joining.SecurityCode);
        Assert.Matches(@"^\d{4} \d{4} \d{4}$", joining.SecurityCode!);
        Assert.Equal("Step 3 of 3", joining.StepText);
        Assert.Equal("laptop-win11 responded. Check that both screens show this code:", inviting.RespondedText);
        laptop.Shell.Navigate(AppPage.Devices);
        Snapshots.Save(laptop.Window, "screen-devices-code");

        await inviting.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("Waiting for laptop-win11 to confirm…", inviting.WaitingText);
        await joining.ConfirmCommand.ExecuteAsync(null);

        await UiAsync.UntilAsync(() => laptop.Page<DevicesViewModel>().Paired.Count == 1 && workstation.Page<DevicesViewModel>().Paired.Count == 1);
        Assert.Equal(PairingStep.Start, joining.Step);
        Assert.Equal("Paired with workstation.", joining.Message);
        Assert.Equal("workstation", laptop.Page<DevicesViewModel>().Paired.Single().Name);
    });

    [Fact]
    public Task Codes_differ_stores_nothing() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var workstation = await cores.StartAsync("workstation");
        var inviting = workstation.Page<DevicesViewModel>().Pairing;
        var joining = laptop.Page<DevicesViewModel>().Pairing;
        inviting.CreateInvitationCommand.Execute(null);
        await inviting.CopyInvitationCommand.ExecuteAsync(null);
        joining.InvitationInput = workstation.Desktop.Copied!;
        await joining.ImportCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => inviting.Step == PairingStep.Code && joining.Step == PairingStep.Code);

        await joining.DifferCommand.ExecuteAsync(null);

        await UiAsync.UntilAsync(() => inviting.Step == PairingStep.Start && joining.Step == PairingStep.Start);
        Assert.Contains("Nothing was stored", inviting.Message, StringComparison.Ordinal);
        Assert.Empty(await laptop.Core.Devices.GetPairedAsync(CancellationToken.None));
        Assert.Empty(await workstation.Core.Devices.GetPairedAsync(CancellationToken.None));
    });

    [Fact]
    public Task Broken_invitation_is_explained() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var pairing = laptop.Page<DevicesViewModel>().Pairing;

        pairing.InvitationInput = "not an invitation";
        await pairing.ImportCommand.ExecuteAsync(null);

        Assert.Equal(PairingStep.Start, pairing.Step);
        Assert.StartsWith("Pairing failed:", pairing.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Devices_block_permissions_and_remove() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var workstation = await cores.StartAsync("workstation");
        await TwoCores.PairAsync(laptop, workstation);
        laptop.Shell.Navigate(AppPage.Devices);
        var devices = laptop.Page<DevicesViewModel>();
        await UiAsync.UntilAsync(() => devices.Paired.Count == 1);
        var device = devices.Paired.Single();
        Assert.StartsWith("SHA256: ", device.Fingerprint, StringComparison.Ordinal);
        Assert.True(device.CanSendToMe);

        // Permissions: "May send to me" off
        _ = device.PermissionsCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => laptop.Shell.Dialogs.Current is PermissionsDialogViewModel);
        var permissions = (PermissionsDialogViewModel)laptop.Shell.Dialogs.Current!;
        permissions.CanSendToMe = false;
        permissions.DoneCommand.Execute(null);
        await UiAsync.UntilAsync(() => devices.Paired.Count == 1 && !devices.Paired[0].CanSendToMe);

        // Block, and the card says so
        await devices.Paired[0].ToggleBlockCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => devices.Paired.Count == 1 && devices.Paired[0].IsBlocked);
        Assert.Equal("Unblock", devices.Paired[0].BlockLabel);
        Assert.Equal(DeviceTrust.Blocked, laptop.Core.Presence.Devices.Single().Trust);
        Snapshots.Save(laptop.Window, "screen-devices-blocked");

        // Remove asks first
        var remove = devices.Paired[0].RemoveCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => laptop.Shell.Dialogs.Current is ConfirmDialogViewModel);
        var confirm = (ConfirmDialogViewModel)laptop.Shell.Dialogs.Current!;
        Assert.Equal("Remove workstation?", confirm.Title);
        Snapshots.Save(laptop.Window, "screen-devices-remove");
        confirm.ConfirmCommand.Execute(null);
        await remove;
        await UiAsync.UntilAsync(() => devices.Paired.Count == 0);
        Assert.DoesNotContain(laptop.Core.Presence.Devices, d => d.State != PresenceState.Found);
    });

    [Fact]
    public Task Settings_save_limits_and_name() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var settings = laptop.Page<SettingsViewModel>();
        laptop.Shell.Navigate(AppPage.Settings);

        Assert.StartsWith("SHA256: ", settings.Fingerprint, StringComparison.Ordinal);
        settings.UploadLimitMegabytes = 12.5m;
        settings.ParallelTransfers = 3;
        settings.DeviceName = "studio";

        var saved = laptop.Core.Settings.Current;
        Assert.Equal(12_500_000, saved.UploadLimitBytesPerSecond);
        Assert.Equal(3, saved.ParallelTransfers);
        Assert.Equal("studio", saved.DeviceName);
        Assert.Equal("studio", laptop.Shell.LocalDeviceName);
        laptop.Window.UpdateLayout();
        Assert.Contains(laptop.Window.GetVisualDescendants().OfType<NumericUpDown>(), n => n.Name == "UploadLimit" && n.Value == 12.5m);
        Snapshots.Save(laptop.Window, "screen-settings");
        await Task.CompletedTask;
    });
}
