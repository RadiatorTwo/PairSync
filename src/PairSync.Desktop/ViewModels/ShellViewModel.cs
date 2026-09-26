using System.Globalization;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Desktop.Resources;
using PairSync.Storage.Settings;

namespace PairSync.Desktop.ViewModels;

/// <summary>What closing the main window does (plan §10: closing and quitting are separate).</summary>
public enum CloseAction
{
    /// <summary>Hide the window; the app keeps running in the tray.</summary>
    Hide,
    Minimize,

    /// <summary>Quit the whole app.</summary>
    Quit,

    /// <summary>Let the window close; the app is already shutting down.</summary>
    Close,
}

/// <summary>Main window: navigation, sidebar status, window title, active dialog and the close behavior.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly Action _quit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private PageViewModel _activePage;


    /// <param name="trayAvailable">False without a tray host; closing then minimizes instead of hiding.</param>
    /// <param name="quit">Shuts the app down (tray and sidebar "Quit PairSync").</param>
    /// <param name="dialogs">Shared with pages that open dialogs; a new host if null.</param>
    public ShellViewModel(SettingsStore settings, bool trayAvailable, IReadOnlyList<PageViewModel> pages, Action quit, DialogHost? dialogs = null)
    {
        Dialogs = dialogs ?? new DialogHost();
        if (pages.Count == 0)
            throw new ArgumentException("The shell needs at least one page.", nameof(pages));
        _settings = settings;
        _quit = quit;
        TrayAvailable = trayAvailable;
        Pages = pages;
        _activePage = pages[0];
        _settings.Changed += OnSettingsChanged;
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>The modal dialog over the content (incoming transfer, confirmations).</summary>
    public DialogHost Dialogs { get; }

    public bool TrayAvailable { get; }

    public string WindowTitle => string.Format(CultureInfo.CurrentCulture, Strings.WindowTitleFormat, ActivePage.NavTitle);

    /// <summary>This device's name under the brand.</summary>
    public string LocalDeviceName => _settings.Current.EffectiveDeviceName;

    public CloseBehavior CloseBehavior => _settings.Current.CloseBehavior;

    /// <summary>Without a tray host "Keep running in tray" behaves like "Minimize".</summary>
    public CloseBehavior EffectiveCloseBehavior =>
        CloseBehavior == CloseBehavior.Tray && !TrayAvailable ? CloseBehavior.Minimize : CloseBehavior;

    /// <summary>Sidebar footer, e.g. "Running. Closing the window keeps the app in the tray."</summary>
    public string StatusText => EffectiveCloseBehavior switch
    {
        CloseBehavior.Tray => Strings.Status_Tray,
        CloseBehavior.Minimize => Strings.Status_Minimize,
        _ => Strings.Status_Quit,
    };

    public T Page<T>() where T : PageViewModel => Pages.OfType<T>().First();

    public void Navigate(AppPage page) => ActivePage = Pages.First(p => p.Page == page);

    partial void OnActivePageChanged(PageViewModel value) => value.OnOpened();

    [RelayCommand]
    private void Quit() => _quit();

    /// <summary>Decides what a close request for the main window does.</summary>
    public CloseAction DecideClose(WindowCloseReason reason)
    {
        // The app or the session is ending: never keep the window.
        if (reason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return CloseAction.Close;
        return EffectiveCloseBehavior switch
        {
            CloseBehavior.Tray => CloseAction.Hide,
            CloseBehavior.Minimize => CloseAction.Minimize,
            _ => CloseAction.Quit,
        };
    }

    private void OnSettingsChanged(AppSettings settings) => Ui.Run(() =>
    {
        OnPropertyChanged(nameof(LocalDeviceName));
        OnPropertyChanged(nameof(CloseBehavior));
        OnPropertyChanged(nameof(EffectiveCloseBehavior));
        OnPropertyChanged(nameof(StatusText));
    });

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        foreach (var page in Pages.OfType<IDisposable>())
            page.Dispose();
    }
}
