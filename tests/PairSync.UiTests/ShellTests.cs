using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PairSync.Desktop;
using PairSync.Desktop.Controls;
using PairSync.Desktop.ViewModels;
using PairSync.Desktop.Views;
using PairSync.Storage.Settings;

namespace PairSync.UiTests;

public sealed class ShellTests(HeadlessFixture ui) : IDisposable
{
    private readonly TempSettings _settings = new();
    private int _quitCalls;

    public void Dispose() => _settings.Dispose();

    private (MainWindow Window, ShellViewModel Shell) Open(bool trayAvailable = true, CloseBehavior close = CloseBehavior.Tray)
    {
        _settings.Store.Update(s => s with { CloseBehavior = close, DeviceName = "workstation-cachyos" });
        var shell = App.CreateShell(_settings.Store, trayAvailable, new FakeAutostart(), () => _quitCalls++);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        return (window, shell);
    }

    private static void Discard(MainWindow window)
    {
        window.DataContext = null; // without a shell the window simply closes
        window.Close();
    }

    [Fact]
    public Task Starts_on_overview_with_the_mockup_sidebar() => ui.RunAsync(() =>
    {
        var (window, shell) = Open();

        Assert.Equal("PairSync — Overview", window.Title);
        Assert.Equal(1024, window.MinWidth);
        Assert.Equal(680, window.MinHeight);
        var navigation = window.FindControl<ListBox>("Navigation")!;
        Assert.Equal(["Overview", "Send", "Syncs", "Claude Code", "Devices", "Settings"],
            shell.Pages.Select(p => p.NavTitle));
        Assert.Equal(212, navigation.FindAncestorOfType<Border>()!.Bounds.Width);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "workstation-cachyos");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Running. Closing the window keeps the app in the tray.");
        Discard(window);
    });

    [Fact]
    public Task Clicking_a_nav_entry_switches_screen_and_title() => ui.RunAsync(() =>
    {
        var (window, shell) = Open();
        var navigation = window.FindControl<ListBox>("Navigation")!;
        var settingsItem = navigation.ContainerFromIndex(5)!;
        var center = settingsItem.TranslatePoint(new Point(settingsItem.Bounds.Width / 2, settingsItem.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);

        Assert.Equal(AppPage.Settings, shell.ActivePage.Page);
        Assert.Equal("PairSync — Settings", window.Title);
        Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
        Discard(window);
    });

    [Fact]
    public Task Later_phases_show_a_note() => ui.RunAsync(() =>
    {
        var (window, shell) = Open();

        shell.Navigate(AppPage.Syncs);
        window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("phase 3", StringComparison.Ordinal) == true);
        shell.Navigate(AppPage.ClaudeCode);
        window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("phase 4", StringComparison.Ordinal) == true);
        Discard(window);
    });

    [Fact]
    public Task Closing_with_tray_hides_the_window_and_keeps_running() => ui.RunAsync(() =>
    {
        var (window, _) = Open(close: CloseBehavior.Tray);

        window.Close();

        Assert.False(window.IsVisible);
        Assert.Equal(0, _quitCalls);
        window.Reveal();
        Assert.True(window.IsVisible);
        Discard(window);
    });

    [Fact]
    public Task Closing_without_a_tray_host_minimizes_instead() => ui.RunAsync(() =>
    {
        var (window, shell) = Open(trayAvailable: false, close: CloseBehavior.Tray);

        window.Close();

        Assert.True(window.IsVisible);
        Assert.Equal(WindowState.Minimized, window.WindowState);
        Assert.Equal(0, _quitCalls);
        Assert.Equal("Running. Closing the window minimizes the app.", shell.StatusText);
        window.Reveal();
        Assert.Equal(WindowState.Normal, window.WindowState);
        Discard(window);
    });

    [Fact]
    public Task Closing_with_quit_quits_the_app() => ui.RunAsync(() =>
    {
        var (window, _) = Open(close: CloseBehavior.Quit);

        window.Close();

        Assert.Equal(1, _quitCalls);
        Discard(window);
    });

    [Fact]
    public Task Quit_link_quits_regardless_of_close_behavior() => ui.RunAsync(() =>
    {
        var (window, _) = Open(close: CloseBehavior.Tray);
        var quit = window.FindControl<Button>("QuitButton")!;

        Assert.Equal("Quit PairSync", quit.Content);
        quit.Command!.Execute(null);

        Assert.Equal(1, _quitCalls);
        Discard(window);
    });

    [Fact]
    public Task Shutdown_closes_the_window_whatever_the_setting()
    {
        var shell = App.CreateShell(_settings.Store, trayAvailable: true, new FakeAutostart(), () => { });

        Assert.Equal(CloseAction.Close, shell.DecideClose(WindowCloseReason.ApplicationShutdown));
        Assert.Equal(CloseAction.Close, shell.DecideClose(WindowCloseReason.OSShutdown));
        Assert.Equal(CloseAction.Hide, shell.DecideClose(WindowCloseReason.WindowClosing));
        return Task.CompletedTask;
    }

    [Fact]
    public Task Changing_the_close_behavior_updates_the_sidebar() => ui.RunAsync(() =>
    {
        var (window, shell) = Open(close: CloseBehavior.Tray);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.Page<SettingsViewModel>().SelectedCloseOption = shell.Page<SettingsViewModel>().CloseOptions[2];

        Assert.Equal(CloseBehavior.Quit, _settings.Open().Current.CloseBehavior);
        Assert.Contains(nameof(ShellViewModel.StatusText), changed);
        Assert.Equal("Running. Closing the window quits PairSync.", shell.StatusText);
        Discard(window);
    });

    [Fact]
    public Task Dialog_overlay_shows_dialogs_one_after_another() => ui.RunAsync(() =>
    {
        var (window, shell) = Open();
        var overlay = window.FindControl<DialogOverlay>("DialogHost")!;
        Assert.False(overlay.IsVisible);

        var first = new ConfirmDialogViewModel("Remove laptop-win11?", "Unfinished transfers are canceled.", "Remove");
        var second = new ConfirmDialogViewModel("Remove nas-box?", "…", "Remove");
        _ = shell.Dialogs.ShowAsync(first);
        _ = shell.Dialogs.ShowAsync(second);
        window.UpdateLayout();

        Assert.True(overlay.IsVisible);
        var frame = overlay.GetVisualDescendants().OfType<Card>().First();
        Assert.Equal(680, frame.Bounds.Width);
        Assert.True(frame.ShowMarks);
        Assert.Contains(overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Remove laptop-win11?");

        first.ConfirmCommand.Execute(null);
        UiWait.Until(() => shell.Dialogs.Current == second);
        Assert.True(first.Confirmed);
        second.CancelCommand.Execute(null);
        UiWait.Until(() => shell.Dialogs.Current is null);
        Assert.False(second.Confirmed);
        Assert.False(overlay.IsVisible);
        Discard(window);
    });
}
