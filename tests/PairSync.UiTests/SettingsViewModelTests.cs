using PairSync.Desktop.ViewModels;
using PairSync.Storage.Settings;

namespace PairSync.UiTests;

public sealed class SettingsViewModelTests(HeadlessFixture ui) : IDisposable
{
    private readonly TempSettings _settings = new();

    public void Dispose() => _settings.Dispose();

    [Fact]
    public Task Close_options_follow_the_mockup_and_save() => ui.RunAsync(() =>
    {
        using var vm = new SettingsViewModel(_settings.Store, new FakeAutostart(), trayAvailable: true);

        Assert.Equal(["Keep running in tray", "Minimize", "Quit"], vm.CloseOptions.Select(o => o.Label));
        Assert.Equal(CloseBehavior.Tray, vm.SelectedCloseOption.Value);
        vm.SelectedCloseOption = vm.CloseOptions[1];

        Assert.Equal(CloseBehavior.Minimize, _settings.Open().Current.CloseBehavior);
        Assert.False(vm.ShowNoTrayBanner);
    });

    [Fact]
    public Task No_tray_host_shows_the_banner() => ui.RunAsync(() =>
    {
        using var vm = new SettingsViewModel(_settings.Store, new FakeAutostart(), trayAvailable: false);

        Assert.True(vm.ShowNoTrayBanner);
    });

    [Fact]
    public Task Autostart_toggle_writes_the_entry_and_the_setting() => ui.RunAsync(() =>
    {
        var autostart = new FakeAutostart();
        using var vm = new SettingsViewModel(_settings.Store, autostart, trayAvailable: true);

        vm.StartWithSystem = true;
        Assert.True(autostart.IsEnabled);
        Assert.True(_settings.Open().Current.StartWithSystem);

        vm.StartWithSystem = false;
        Assert.False(autostart.IsEnabled);
        Assert.False(_settings.Open().Current.StartWithSystem);
    });

    [Fact]
    public Task Failed_autostart_reverts_and_explains() => ui.RunAsync(() =>
    {
        var autostart = new FakeAutostart { Failure = new IOException("access denied") };
        using var vm = new SettingsViewModel(_settings.Store, autostart, trayAvailable: true);

        vm.StartWithSystem = true;

        Assert.False(vm.StartWithSystem);
        Assert.False(_settings.Open().Current.StartWithSystem);
        Assert.Contains("access denied", vm.AutostartError, StringComparison.Ordinal);
    });

    [Fact]
    public Task Stun_servers_are_validated_added_removed_and_reset() => ui.RunAsync(() =>
    {
        using var vm = new SettingsViewModel(_settings.Store, new FakeAutostart(), trayAvailable: true);
        Assert.Equal(AppSettings.DefaultStunServers, vm.StunServers.Select(s => s.Uri));

        vm.NewStunServer = "turn:relay.example.org";
        vm.AddStunServerCommand.Execute(null);
        Assert.Equal("Only plain STUN over UDP (stun:) is supported.", vm.StunError);

        vm.NewStunServer = "stun.example.org:3479";
        vm.AddStunServerCommand.Execute(null);
        Assert.Null(vm.StunError);
        Assert.Equal("", vm.NewStunServer);
        Assert.Equal("stun:stun.example.org:3479", _settings.Open().Current.StunServers[^1]);

        vm.NewStunServer = "STUN:stun.example.org:3479";
        vm.AddStunServerCommand.Execute(null);
        Assert.Equal("This server is already in the list.", vm.StunError);

        foreach (var row in vm.StunServers.ToList())
            row.RemoveCommand.Execute(null);
        Assert.Empty(_settings.Open().Current.StunServers);
        Assert.False(vm.HasStunServers);

        vm.ResetStunServersCommand.Execute(null);
        Assert.Equal(AppSettings.DefaultStunServers, _settings.Open().Current.StunServers);
        Assert.Equal(2, vm.StunServers.Count);
    });

    [Fact]
    public Task External_changes_are_picked_up() => ui.RunAsync(() =>
    {
        using var vm = new SettingsViewModel(_settings.Store, new FakeAutostart(), trayAvailable: true);

        _settings.Store.Update(s => s with { CloseBehavior = CloseBehavior.Quit });

        Assert.Equal(CloseBehavior.Quit, vm.SelectedCloseOption.Value);
    });
}
