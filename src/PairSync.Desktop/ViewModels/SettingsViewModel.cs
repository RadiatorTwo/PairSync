using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Application;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Storage.Secrets;
using PairSync.Storage.Settings;
using PairSync.Stun;

namespace PairSync.Desktop.ViewModels;

/// <summary>A STUN server in the "Internet connections" list.</summary>
public sealed record StunServerRow(string Uri, IRelayCommand RemoveCommand);

/// <summary>
/// Settings screen (max. 640 px): device, close behavior, autostart, network, internet connections (STUN, relay,
/// NAT diagnostic), limits, logs.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly IAutostart _autostart;
    private readonly PairSyncCore? _core;
    private readonly IDesktopServices? _desktop;
    private readonly InternetUi? _internet;
    private bool _loading;

    [ObservableProperty]
    private string _newStunServer = "";

    /// <summary>Why the entered STUN server was not added.</summary>
    [ObservableProperty]
    private string? _stunError;

    [ObservableProperty]
    private Choice<CloseBehavior> _selectedCloseOption;

    [ObservableProperty]
    private bool _startWithSystem;

    /// <summary>Why the autostart entry could not be changed; null when it worked.</summary>
    [ObservableProperty]
    private string? _autostartError;

    [ObservableProperty]
    private string _deviceName = "";

    [ObservableProperty]
    private decimal? _port;

    [ObservableProperty]
    private decimal? _uploadLimitMegabytes;

    [ObservableProperty]
    private decimal? _parallelTransfers;

    [ObservableProperty]
    private bool _verboseLogging;

    /// <param name="core">Null in tests that only cover close behavior and autostart.</param>
    /// <param name="internet">Opens the NAT diagnostic; null leaves the button out.</param>
    public SettingsViewModel(
        SettingsStore settings, IAutostart autostart, bool trayAvailable, PairSyncCore? core = null, IDesktopServices? desktop = null,
        InternetUi? internet = null)
        : base(AppPage.Settings)
    {
        _settings = settings;
        _autostart = autostart;
        _core = core;
        _desktop = desktop;
        _internet = internet;
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

    public string? Fingerprint => _core?.Identity.Identity.Fingerprint.ToShortString();

    /// <summary>No keyring on Linux: the key is in a 0600 file.</summary>
    public bool KeyInFile => _core?.Identity.Protection == SecretProtection.File;

    public string PortHint => string.Format(CultureInfo.CurrentCulture, Strings.Settings_PortHint, _settings.Current.Port);

    public string? ListenError => _core?.Lan.ListenError is { } error
        ? string.Format(CultureInfo.CurrentCulture, Strings.Settings_ListenError, error)
        : null;

    public string? DiscoveryError => _core?.Presence.DiscoveryError is { } error
        ? string.Format(CultureInfo.CurrentCulture, Strings.Settings_DiscoveryError, error)
        : null;

    public bool CanOpenLogs => _core is not null && _desktop is not null;

    public ObservableCollection<StunServerRow> StunServers { get; } = [];

    public bool HasStunServers => StunServers.Count > 0;

    public bool CanAddStunServer => StunServers.Count < AppSettings.MaxStunServers;

    public bool CanRunDiagnostic => _internet is not null;

    /// <summary>The native WebRTC library is missing: internet connections are off, with the reason.</summary>
    public string? InternetUnavailable => _core is { Internet.IsAvailable: false } core
        ? string.Format(CultureInfo.CurrentCulture, Strings.Settings_InternetUnavailable, core.Internet.UnavailableReason)
        : null;

    [RelayCommand]
    private void AddStunServer()
    {
        if (!StunServerUri.TryParse(NewStunServer.Trim(), out var uri, out var error))
        {
            StunError = error switch
            {
                StunUriError.Empty => Strings.Stun_Empty,
                StunUriError.UnsupportedScheme => Strings.Stun_Scheme,
                StunUriError.InvalidPort => Strings.Stun_Port,
                _ => Strings.Stun_Invalid,
            };
            return;
        }
        var text = uri.ToString();
        if (_settings.Current.StunServers.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            StunError = Strings.Stun_Duplicate;
            return;
        }
        if (!CanAddStunServer)
        {
            StunError = string.Format(CultureInfo.CurrentCulture, Strings.Stun_TooMany, AppSettings.MaxStunServers);
            return;
        }
        StunError = null;
        NewStunServer = "";
        _settings.Update(s => s with { StunServers = [.. s.StunServers, text] });
    }

    private void RemoveStunServer(string uri)
    {
        StunError = null;
        _settings.Update(s => s with { StunServers = [.. s.StunServers.Where(x => !string.Equals(x, uri, StringComparison.OrdinalIgnoreCase))] });
    }

    [RelayCommand]
    private void ResetStunServers()
    {
        StunError = null;
        _settings.Update(s => s with { StunServers = AppSettings.DefaultStunServers });
    }

    [RelayCommand]
    private Task RunDiagnosticAsync() => _internet?.ShowDiagnosticAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private async Task OpenLogsAsync()
    {
        if (_core is not null && _desktop is not null)
            await _desktop.OpenFolderAsync(_core.DataDirectory.LogsDirectory);
    }

    partial void OnDeviceNameChanged(string value) => Save(s => s with { DeviceName = value });

    partial void OnPortChanged(decimal? value)
    {
        if (value is { } port)
            Save(s => s with { Port = (int)port });
    }

    partial void OnUploadLimitMegabytesChanged(decimal? value) =>
        Save(s => s with { UploadLimitBytesPerSecond = (long)((value ?? 0) * 1_000_000) });

    partial void OnParallelTransfersChanged(decimal? value)
    {
        if (value is { } count)
            Save(s => s with { ParallelTransfers = (int)count });
    }

    partial void OnVerboseLoggingChanged(bool value) => Save(s => s with { VerboseLogging = value });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_loading)
            _settings.Update(change);
    }

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
            DeviceName = settings.DeviceName ?? settings.EffectiveDeviceName;
            Port = settings.Port;
            UploadLimitMegabytes = settings.UploadLimitBytesPerSecond / 1_000_000m;
            ParallelTransfers = settings.ParallelTransfers;
            VerboseLogging = settings.VerboseLogging;
            if (!StunServers.Select(r => r.Uri).SequenceEqual(settings.StunServers))
            {
                StunServers.Clear();
                foreach (var server in settings.StunServers)
                    StunServers.Add(new StunServerRow(server, new RelayCommand(() => RemoveStunServer(server))));
                OnPropertyChanged(nameof(HasStunServers));
                OnPropertyChanged(nameof(CanAddStunServer));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
