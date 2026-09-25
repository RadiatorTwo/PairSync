using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PairSync.Application;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Desktop.Tray;
using PairSync.Desktop.ViewModels;
using PairSync.Storage.Settings;

namespace PairSync.Desktop;

// Fully qualified: inside PairSync.* the name "Application" means the PairSync.Application namespace.
public sealed partial class App : Avalonia.Application
{
    private ShellViewModel? _shell;
    private MainWindow? _window;
    private TrayController? _tray;
    private CoreEvents? _events;

    /// <summary>Null in the designer.</summary>
    public PairSyncCore? Core { get; init; }

    /// <summary>False when no tray host exists (Linux without StatusNotifierWatcher).</summary>
    public bool TrayAvailable { get; init; } = true;

    /// <summary>Started from the login entry: stay in the tray.</summary>
    public bool StartHidden { get; init; }

    public SingleInstance? Instance { get; init; }

    public IAutostart? Autostart { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Core is { } core)
                StartDesktop(desktop, core);
            else
                desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The shell without a core (designer, shell tests): later screens are placeholders.</summary>
    internal static ShellViewModel CreateShell(SettingsStore settings, bool trayAvailable, IAutostart autostart, Action quit) =>
        new(settings, trayAvailable,
        [
            new PlaceholderViewModel(AppPage.Overview),
            new PlaceholderViewModel(AppPage.Send, Strings.Title_Send),
            SyncsPage(),
            ClaudeCodePage(),
            new PlaceholderViewModel(AppPage.Devices),
            new SettingsViewModel(settings, autostart, trayAvailable),
        ], quit);

    /// <summary>The screens of the main window, in navigation order.</summary>
    internal static ShellViewModel CreateShell(
        PairSyncCore core, bool trayAvailable, IAutostart autostart, IDesktopServices desktop, TimeProvider time, Action quit)
    {
        ShellViewModel? shell = null;
        void Navigate(AppPage page) => shell?.Navigate(page);
        var dialogs = new DialogHost();
        var devices = new DevicesViewModel(core, dialogs, desktop, time);
        shell = new ShellViewModel(core.Settings, trayAvailable,
        [
            new OverviewViewModel(core, device =>
            {
                Navigate(AppPage.Devices);
                return devices.Pairing.PairWithAsync(device);
            }, new LatencyProbe(), time),
            new SendViewModel(core, desktop, Navigate),
            SyncsPage(),
            ClaudeCodePage(),
            devices,
            new SettingsViewModel(core.Settings, autostart, trayAvailable, core, desktop),
        ], quit, dialogs);
        return shell;
    }

    private static PlaceholderViewModel SyncsPage() => new(AppPage.Syncs, Strings.Title_Syncs, Strings.Syncs_ComingLater);

    private static PlaceholderViewModel ClaudeCodePage() => new(AppPage.ClaudeCode, message: Strings.ClaudeCode_ComingLater);

    private void StartDesktop(IClassicDesktopStyleApplicationLifetime desktop, PairSyncCore core)
    {
        // Closing the window never ends the app by itself; only Quit does (plan §10).
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var autostart = Autostart ?? Platform.Autostart.ForCurrentPlatform();
        ApplyAutostart(core, autostart);

        var icon = AppIcon.Create();
        var services = new WindowDesktopServices(() => _window, RevealWindow);
        _shell = CreateShell(core, TrayAvailable, autostart, services, TimeProvider.System, () => desktop.Shutdown());
        _window = new MainWindow { DataContext = _shell, Icon = icon };
        _events = new CoreEvents(core, _shell, services);
        if (TrayAvailable)
            _tray = new TrayController(this, core, icon, RevealWindow, () => desktop.Shutdown());

        if (StartHidden && TrayAvailable)
        {
            // Shown later from the tray or a second start.
        }
        else
        {
            if (StartHidden)
                _window.WindowState = WindowState.Minimized;
            desktop.MainWindow = _window;
        }

        Instance?.SetActivationHandler(() => Dispatcher.UIThread.Post(RevealWindow));
        desktop.Exit += (_, _) =>
        {
            _events?.Dispose();
            _tray?.Dispose();
            _shell?.Dispose();
        };
    }

    private void RevealWindow() => _window?.Reveal();

    /// <summary>The setting is the truth: recreate the entry (the program may have moved) or remove a stale one.</summary>
    private static void ApplyAutostart(PairSyncCore core, IAutostart autostart)
    {
        var wanted = core.Settings.Current.StartWithSystem;
        if (!autostart.IsSupported || (!wanted && !autostart.IsEnabled))
            return;
        try
        {
            autostart.Set(wanted);
        }
        catch (IOException e)
        {
            core.Services.GetRequiredService<ILogger<App>>().LogWarning(e, "Autostart entry could not be updated");
        }
    }
}
