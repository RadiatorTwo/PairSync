using System.Net;
using PairSync.Application.Internet;
using PairSync.Desktop.ViewModels;
using PairSync.Stun;

namespace PairSync.UiTests;

/// <summary>
/// Internet connections on the screens (phase 2 block F): code exchange in the connect dialog, pairing with an
/// internet invitation, NAT banner. WebRTC runs over host candidates on this machine; no STUN request leaves it.
/// </summary>
public sealed class InternetScreenTests(HeadlessFixture ui)
{
    private static ConnectDialogViewModel Dialog(TestApp app) =>
        Assert.IsType<ConnectDialogViewModel>(app.Shell.Dialogs.Current);

    private static DeviceCardViewModel CardOf(TestApp app, TestApp other) =>
        app.Page<OverviewViewModel>().Devices.Single(d => d.Id == other.Id);

    /// <summary>A opens "Connect via internet…", B pastes the code, A pastes the answer.</summary>
    private static async Task ExchangeCodesAsync(TestApp a, TestApp b)
    {
        var overview = a.Page<OverviewViewModel>();
        await UiAsync.UntilAsync(() => overview.Devices.Count == 1 && CardOf(a, b).HasActions);
        var connect = CardOf(a, b).Actions.Single(x => x.Label == "Connect via internet…");
        _ = connect.Command.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => a.Shell.Dialogs.Current is ConnectDialogViewModel { IsCode: true });
        var offering = Dialog(a);
        await offering.CopyCommand.ExecuteAsync(null);
        Assert.StartsWith("PSC1:", a.Desktop.Copied, StringComparison.Ordinal);
        Snapshots.Save(a.Window, "screen-connect-code");

        _ = b.Page<OverviewViewModel>().PasteCodeCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => b.Shell.Dialogs.Current is ConnectDialogViewModel);
        var answering = Dialog(b);
        Assert.True(answering.IsEnterCode);
        answering.Input = a.Desktop.Copied!;
        await answering.SubmitCommand.ExecuteAsync(null);
        Assert.True(answering.IsAnswer, answering.Message);
        await answering.CopyCommand.ExecuteAsync(null);
        Assert.StartsWith("PSR1:", b.Desktop.Copied, StringComparison.Ordinal);

        offering.Input = b.Desktop.Copied!;
        await offering.SubmitCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => offering.IsConnected && answering.IsConnected);
    }

    [Fact]
    public Task Connect_via_internet_on_both_screens() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var office = await cores.StartAsync("office-pc");
        await TwoCores.PairAsync(laptop, office);

        await UiAsync.UntilAsync(() => laptop.Page<OverviewViewModel>().Devices.Count == 1);
        Assert.Equal(CardKind.Offline, CardOf(laptop, office).Kind);

        await ExchangeCodesAsync(laptop, office);

        var offering = Dialog(laptop);
        Assert.Equal("Connected to office-pc over the internet", offering.StatusText);
        Assert.Equal("Route: host candidate · manual code", offering.RouteText);
        Assert.Equal("Step 3 of 3", offering.StepText);
        laptop.Shell.Navigate(AppPage.Overview);
        Snapshots.Save(laptop.Window, "screen-connect-dialog");
        offering.DismissCommand.Execute(null);
        Dialog(office).DismissCommand.Execute(null);

        await UiAsync.UntilAsync(() => CardOf(laptop, office).IsConnected && CardOf(office, laptop).IsConnected);
        Assert.StartsWith("Internet · direct", CardOf(laptop, office).Line1, StringComparison.Ordinal);
        Assert.Equal("Route: host candidate · manual code", CardOf(laptop, office).Line2);
        await UiAsync.UntilAsync(() => CardOf(laptop, office).Line1.EndsWith(" ms", StringComparison.Ordinal));
        Snapshots.Save(laptop.Window, "screen-overview-internet");

        var send = laptop.Page<SendViewModel>();
        await UiAsync.UntilAsync(() => send.Target is { IsOnline: true, ViaInternet: true });
        Assert.StartsWith("Internet direct", send.RouteText, StringComparison.Ordinal);

        // Disconnecting ends the link on both sides. The other side was told: plain offline, no "connection lost",
        // and the card still offers a new connection.
        await CardOf(laptop, office).Actions.Single().Command.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => !CardOf(laptop, office).IsConnected);
        await UiAsync.UntilAsync(() => !CardOf(office, laptop).IsConnected);
        Assert.Null(CardOf(office, laptop).InternetText);
        Assert.Contains(CardOf(office, laptop).Actions, x => x is { Label: "Connect via internet…", IsPrimary: false });
        Assert.Null(office.Page<OverviewViewModel>().NatBanner);
    });

    [Fact]
    public Task Waiting_code_shows_on_the_card_and_takes_the_answer_later() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var office = await cores.StartAsync("office-pc");
        await TwoCores.PairAsync(laptop, office);

        var code = await laptop.Core.Internet.CreateCodeAsync(office.Id, CancellationToken.None);
        await UiAsync.UntilAsync(() => laptop.Page<OverviewViewModel>().Devices.Count == 1
                                       && CardOf(laptop, office).InternetText?.StartsWith("Waiting for answer · expires in 09:", StringComparison.Ordinal) == true);
        Assert.Equal(["Paste answer…", "Cancel"], CardOf(laptop, office).Actions.Select(x => x.Label));

        var (_, answer) = await office.Core.Internet.AnswerCodeAsync(code.Text, CancellationToken.None);
        await UiAsync.UntilAsync(() => CardOf(office, laptop).InternetText == "Waiting for laptop-win11 to apply the answer");

        _ = CardOf(laptop, office).Actions[0].Command.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => laptop.Shell.Dialogs.Current is ConnectDialogViewModel);
        var dialog = Dialog(laptop);
        Assert.Equal("Paste the answer from office-pc", dialog.Title);
        dialog.Input = "PSC1:not-an-answer";
        await dialog.SubmitCommand.ExecuteAsync(null);
        Assert.NotNull(dialog.Message);
        dialog.Input = answer.Text;
        await dialog.SubmitCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => dialog.IsConnected);
        dialog.DismissCommand.Execute(null);
        await UiAsync.UntilAsync(() => CardOf(office, laptop).IsConnected);
    });

    [Fact]
    public Task Failed_connection_shows_the_nat_banner() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var laptop = await cores.StartAsync("laptop-win11");
        var office = await cores.StartAsync("office-pc");
        await TwoCores.PairAsync(laptop, office);

        var code = await laptop.Core.Internet.CreateCodeAsync(office.Id, CancellationToken.None);
        var (_, answer) = await office.Core.Internet.AnswerCodeAsync(code.Text, CancellationToken.None);
        // The other side gives up before the answer arrives: ICE finds nobody.
        await office.Core.Internet.CloseAsync(laptop.Id);
        await Assert.ThrowsAsync<InternetConnectException>(() => laptop.Core.Internet.ApplyAnswerAsync(answer.Text, CancellationToken.None));

        var overview = laptop.Page<OverviewViewModel>();
        await UiAsync.UntilAsync(() => overview.NatBanner is not null);
        // Without STUN servers in tests the analysis names the missing public address.
        Assert.StartsWith("Can't connect to office-pc: no public address found here.", overview.NatBanner!.Headline, StringComparison.Ordinal);
        Assert.NotEmpty(overview.NatBanner.Detail);
        Assert.Equal("No connection · new code needed", CardOf(laptop, office).InternetText);
        Snapshots.Save(laptop.Window, "screen-overview-nat");

        _ = overview.AboutRelaysCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => laptop.Shell.Dialogs.Current is AboutRelaysDialogViewModel);
        ((AboutRelaysDialogViewModel)laptop.Shell.Dialogs.Current!).DoneCommand.Execute(null);

        overview.DismissNatBannerCommand.Execute(null);
        await UiAsync.UntilAsync(() => overview.NatBanner is null);
        Assert.Null(CardOf(laptop, office).InternetText);
    });

    [Fact]
    public Task Pairing_over_the_internet_on_both_screens() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        // The STUN server on loopback never answers; it only makes the invitation an internet invitation.
        var workstation = await cores.StartAsync("workstation", lanInvitations: false, stunServers: ["stun:127.0.0.1:9"]);
        var laptop = await cores.StartAsync("laptop-win11");
        var inviting = workstation.Page<DevicesViewModel>().Pairing;
        var joining = laptop.Page<DevicesViewModel>().Pairing;

        await inviting.CreateInvitationCommand.ExecuteAsync(null);
        Assert.Equal(PairingStep.Invitation, inviting.Step);
        Assert.True(inviting.InvitationIsInternet);
        Assert.Equal("Step 2 of 4", inviting.StepText);
        await inviting.CopyInvitationCommand.ExecuteAsync(null);
        Assert.StartsWith("PSI2:", workstation.Desktop.Copied, StringComparison.Ordinal);
        workstation.Shell.Navigate(AppPage.Devices);
        Snapshots.Save(workstation.Window, "screen-devices-internet-invitation");

        joining.InvitationInput = workstation.Desktop.Copied!;
        _ = joining.ImportCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => joining.Step == PairingStep.AnswerCode);
        Assert.Equal("Step 2 of 4", joining.StepText);
        await joining.CopyInvitationCommand.ExecuteAsync(null);
        Assert.StartsWith("PSR1:", laptop.Desktop.Copied, StringComparison.Ordinal);

        inviting.AnswerInput = laptop.Desktop.Copied!;
        await inviting.ApplyAnswerCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => inviting.Step == PairingStep.Code && joining.Step == PairingStep.Code);
        Assert.Equal(inviting.SecurityCode, joining.SecurityCode);
        Assert.Equal("Step 4 of 4", inviting.StepText);

        await inviting.ConfirmCommand.ExecuteAsync(null);
        await joining.ConfirmCommand.ExecuteAsync(null);
        await UiAsync.UntilAsync(() => laptop.Page<DevicesViewModel>().Paired.Count == 1 && workstation.Page<DevicesViewModel>().Paired.Count == 1);
        Assert.Equal("Paired with workstation. The internet connection stays open.", joining.Message);

        // The pairing connection stays as the internet link.
        await UiAsync.UntilAsync(() => laptop.Page<OverviewViewModel>().Devices.Count == 1 && CardOf(laptop, workstation).IsConnected);
        Assert.StartsWith("Internet · direct", CardOf(laptop, workstation).Line1, StringComparison.Ordinal);
    });

    [Fact]
    public Task Nat_diagnostic_lists_the_checks_and_leaves_out_the_address() => ui.RunAsync(async () =>
    {
        var desktop = new FakeDesktop();
        var report = new NatReport
        {
            Servers =
            [
                new StunServerCheck { Server = "stun:stun.cloudflare.com:3478", Status = StunServerStatus.Answered },
                new StunServerCheck { Server = "stun:stun.l.google.com:19302", Status = StunServerStatus.DnsBlocked },
            ],
            Mapping = NatMappingBehavior.EndpointDependent,
            UdpBlocked = false,
            DnsFilterSuspected = true,
            CgnatSuspected = true,
            PublicEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51000),
            HasGlobalIpv6Address = false,
            Ipv6Checked = true,
            Ipv6StunReachable = false,
            Hint = NatHint.Symmetric,
            StartedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(2),
        };
        using var dialog = new NatDiagnosticDialogViewModel(["stun:stun.cloudflare.com:3478", "stun:stun.l.google.com:19302"], desktop,
            (_, _) => Task.FromResult(report));
        await dialog.Completion;
        await UiAsync.UntilAsync(() => dialog.IsDone);

        Assert.Contains(dialog.Rows, r => r is { Check: "STUN stun:stun.l.google.com:19302", Result: "blocked by a DNS filter", IsProblem: true });
        Assert.Contains(dialog.Rows, r => r is { Check: "NAT mapping", IsProblem: true });
        Assert.StartsWith("This network uses symmetric NAT", dialog.Summary, StringComparison.Ordinal);

        await dialog.CopyReportCommand.ExecuteAsync(null);
        Assert.DoesNotContain("203.0.113.7", desktop.Copied, StringComparison.Ordinal);
        dialog.IncludePublicAddress = true;
        await dialog.CopyReportCommand.ExecuteAsync(null);
        Assert.Contains("203.0.113.7:51000", desktop.Copied, StringComparison.Ordinal);

        using var empty = new NatDiagnosticDialogViewModel([], desktop);
        await empty.Completion;
        Assert.StartsWith("No STUN servers are set.", empty.Summary, StringComparison.Ordinal);
        Assert.Empty(empty.Rows);
    });

    [Fact]
    public Task Wrong_answer_keeps_the_internet_invitation_open() => ui.RunAsync(async () =>
    {
        await using var cores = new TwoCores();
        var workstation = await cores.StartAsync("workstation", lanInvitations: false, stunServers: ["stun:127.0.0.1:9"]);
        var inviting = workstation.Page<DevicesViewModel>().Pairing;
        await inviting.CreateInvitationCommand.ExecuteAsync(null);

        inviting.AnswerInput = "PSR1:damaged";
        await inviting.ApplyAnswerCommand.ExecuteAsync(null);

        Assert.Equal(PairingStep.Invitation, inviting.Step);
        Assert.NotNull(inviting.AnswerMessage);
        inviting.CloseCommand.Execute(null);
        Assert.Equal(PairingStep.Start, inviting.Step);
    });
}
