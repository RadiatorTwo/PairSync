using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Storage.Settings;

namespace PairSync.Desktop.ViewModels;

/// <summary>Settings screen (max. 640 px): close behavior, autostart and the no-tray banner.</summary>
public sealed partial class SettingsViewModel : PageViewModel, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly IAutostart _autostart;
    private bool _loading;

    [ObservableProperty]
    private Choice<CloseBehavior> _selectedCloseOption;

    [ObservableProperty]
    private bool _startWithSystem;

    /// <summary>Why the autostart entry could not be changed; null when it worked.</summary>
    [ObservableProperty]
    private string? _autostartError;

    public SettingsViewModel(SettingsStore settings, IAutostart autostart, bool trayAvailable) : base(AppPage.Settings)
    {
        _settings = settings;
        _autostart = autostart;
        TrayAvailable = trayAvailable;
        CloseOptions =
        [
            new(CloseBehavior.Tray, Strings.Close_Tray),
            new(CloseBehavior.Minimize, Strings.Close_Minimize),
            new(CloseBehavior.Quit, Strings.Close_Quit),
        ];
        _selectedCloseOption = CloseOptions[0];
        Load(settings.Current);
        _settings.Changed += OnSettingsChanged;
    }

    public IReadOnlyList<Choice<CloseBehavior>> CloseOptions { get; }

    public bool TrayAvailable { get; }

    /// <summary>"No system tray found …" banner.</summary>
    public bool ShowNoTrayBanner => !TrayAvailable;

    public bool AutostartSupported => _autostart.IsSupported;

    partial void OnSelectedCloseOptionChanged(Choice<CloseBehavior> value)
    {
        if (!_loading && value is not null)
            _settings.Update(s => s with { CloseBehavior = value.Value });
    }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (_loading)
            return;
        try
        {
            _autostart.Set(value);
            AutostartError = null;
            _settings.Update(s => s with { StartWithSystem = value });
        }
        catch (IOException e)
        {
            AutostartError = string.Format(CultureInfo.CurrentCulture, Strings.Settings_AutostartFailed, e.Message);
            Load(_settings.Current);
        }
    }

    private void OnSettingsChanged(AppSettings settings) => Ui.Run(() => Load(settings));

    private void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            SelectedCloseOption = CloseOptions.First(o => o.Value == settings.CloseBehavior);
            StartWithSystem = settings.StartWithSystem;
        }
        finally
        {
            _loading = false;
        }
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
