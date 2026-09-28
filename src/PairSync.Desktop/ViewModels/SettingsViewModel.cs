using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Application;
using PairSync.Application.Updates;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Storage.Identity;
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
    private readonly UpdateCheck _updates;
    private readonly DialogHost? _dialogs;
    private readonly Action? _quit;
    private bool _loading;

    [ObservableProperty]
    private string _newStunServer = "";

    /// <summary>Why the entered STUN server was not added.</summary>
    [ObservableProperty]
    private string? _stunError;

    [ObservableProperty]
    private Choice<CloseBehavior> _selectedCloseOption;

    [ObservableProperty]
    private Choice<AppTheme> _selectedThemeOption;

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

    /// <summary>Where received files go; below it one subfolder per device.</summary>
    [ObservableProperty]
    private string _receiveFolder = "";

    /// <summary>The user picked a folder; "Use default" goes back to <c>Downloads/PairSync</c>.</summary>
    [ObservableProperty]
    private bool _hasCustomReceiveFolder;

    [ObservableProperty]
    private bool _verboseLogging;

    /// <summary>Result of the last update check; null before the first one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenReleasePage))]
    private UpdateResult? _update;

    [ObservableProperty]
    private string? _updateText;

    /// <summary>Result of the last identity export or import.</summary>
    [ObservableProperty]
    private string? _identityMessage;

    /// <summary><c>NAME=value</c> lines for Claude Code commands from other devices.</summary>
    [ObservableProperty]
    private string _pathVariables = "";

    /// <param name="core">Null in tests that only cover close behavior and autostart.</param>
    /// <param name="internet">Opens the NAT diagnostic; null leaves the button out.</param>
    /// <param name="dialogs">For the identity backup dialogs; null leaves the backup buttons out.</param>
    /// <param name="quit">Quits the app after an identity import.</param>
    public SettingsViewModel(
        SettingsStore settings, IAutostart autostart, bool trayAvailable, PairSyncCore? core = null, IDesktopServices? desktop = null,
        InternetUi? internet = null, UpdateCheck? updates = null, DialogHost? dialogs = null, Action? quit = null)
        : base(AppPage.Settings)
    {
        _dialogs = dialogs;
        _quit = quit;
        _updates = updates ?? new UpdateCheck();
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
        ThemeOptions =
        [
            new(AppTheme.System, Strings.Theme_System),
            new(AppTheme.Light, Strings.Theme_Light),
            new(AppTheme.Dark, Strings.Theme_Dark),
        ];
        _selectedThemeOption = ThemeOptions[0];
        Load(settings.Current);
        _settings.Changed += OnSettingsChanged;
    }

    public IReadOnlyList<Choice<CloseBehavior>> CloseOptions { get; }

    /// <summary>"System / Light / Dark"; the app switches at once (App.axaml.cs follows the setting).</summary>
    public IReadOnlyList<Choice<AppTheme>> ThemeOptions { get; }

    public bool TrayAvailable { get; }

    /// <summary>"No system tray found …" banner.</summary>
    public bool ShowNoTrayBanner => !TrayAvailable;

    public bool AutostartSupported => _autostart.IsSupported;

    public string? Fingerprint => _core?.Identity.Identity.Fingerprint.ToShortString();

    /// <summary>No keyring on Linux: the key is in a 0600 file.</summary>
    public bool KeyInFile => _core?.Identity.Protection == SecretProtection.File;

    public bool CanChooseReceiveFolder => _core is not null && _desktop is not null;

    [RelayCommand]
    private async Task ChooseReceiveFolderAsync()
    {
        if (_desktop is null)
            return;
        if (await _desktop.PickFolderAsync(ReceiveFolder.Length > 0 && Directory.Exists(ReceiveFolder) ? ReceiveFolder : null) is { } folder)
            _settings.Update(s => s with { ReceiveFolder = folder });
    }

    [RelayCommand]
    private void UseDefaultReceiveFolder() => _settings.Update(s => s with { ReceiveFolder = null });

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

    public string VersionText => string.Format(CultureInfo.CurrentCulture, Strings.Settings_Version, AppInfo.DisplayVersion);

    public bool CanOpenReleasePage => _desktop is not null && Update?.ReleaseUrl is not null;

    /// <summary>Only on click (plan phase 6): asks GitHub for the latest release, never downloads anything.</summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        UpdateText = Strings.Update_Checking;
        var result = await _updates.CheckAsync(AppInfo.Version, CancellationToken.None);
        Update = result;
        UpdateText = result.State switch
        {
            UpdateState.NewerAvailable => string.Format(CultureInfo.CurrentCulture, Strings.Update_Newer, result.LatestVersion),
            UpdateState.UpToDate => string.Format(CultureInfo.CurrentCulture, Strings.Update_UpToDate, AppInfo.DisplayVersion),
            UpdateState.NoRelease => Strings.Update_NoRelease,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Update_Failed, result.Error),
        };
    }

    [RelayCommand]
    private async Task OpenReleasePageAsync()
    {
        if (_desktop is not null && Update?.ReleaseUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            await _desktop.OpenUriAsync(uri);
    }

    public bool CanBackUpIdentity => _core is not null && _desktop is not null && _dialogs is not null;

    /// <summary>"Export identity…": password twice, then where to save the backup.</summary>
    [RelayCommand]
    private async Task ExportIdentityAsync()
    {
        if (_core is null || _desktop is null || _dialogs is null)
            return;
        var dialog = new IdentityPasswordDialogViewModel(export: true);
        await _dialogs.ShowAsync(dialog);
        if (dialog.Result is not { } password)
            return;
        var path = await _desktop.PickSaveFileAsync(_settings.Current.EffectiveDeviceName + IdentityBackup.FileExtension, IdentityBackup.FileExtension);
        if (path is null)
            return;
        try
        {
            var content = _core.CreateIdentityBackup(password);
            await File.WriteAllBytesAsync(path, content);
            IdentityMessage = string.Format(CultureInfo.CurrentCulture, Strings.Identity_Saved, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            IdentityMessage = string.Format(CultureInfo.CurrentCulture, Strings.Identity_Failed, e.Message);
        }
    }

    /// <summary>"Import identity…": pick the backup, enter its password, confirm, then quit so the next start uses it.</summary>
    [RelayCommand]
    private async Task ImportIdentityAsync()
    {
        if (_core is null || _desktop is null || _dialogs is null)
            return;
        var path = await _desktop.PickOpenFileAsync(IdentityBackup.FileExtension);
        if (path is null)
            return;
        byte[] content;
        try
        {
            if (new FileInfo(path).Length > IdentityBackup.MaxFileSize)
                throw new IOException(Strings.Code_FileTooLarge);
            content = await File.ReadAllBytesAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            IdentityMessage = string.Format(CultureInfo.CurrentCulture, Strings.Identity_ReadFailed, e.Message);
            return;
        }

        RestoredIdentity? restored = null;
        var dialog = new IdentityPasswordDialogViewModel(export: false, password =>
        {
            try
            {
                restored = IdentityBackup.Open(content, password);
                return null;
            }
            catch (InvalidIdentityBackupException e)
            {
                return e.Message;
            }
        });
        await _dialogs.ShowAsync(dialog);
        if (restored is null)
            return;
        if (restored.DeviceId == _core.Identity.Identity.Id)
        {
            IdentityMessage = Strings.Identity_SameDevice;
            return;
        }

        var confirm = new ConfirmDialogViewModel(Strings.Identity_ReplaceTitle,
            string.Format(CultureInfo.CurrentCulture, Strings.Identity_ReplaceText, restored.DeviceId, restored.Fingerprint.ToShortString()),
            Strings.Identity_ReplaceConfirm);
        await _dialogs.ShowAsync(confirm);
        if (!confirm.Confirmed)
            return;
        try
        {
            await _core.RestoreIdentityAsync(restored, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or SecretStoreUnavailableException)
        {
            IdentityMessage = string.Format(CultureInfo.CurrentCulture, Strings.Identity_ReadFailed, e.Message);
            return;
        }

        var restart = new ConfirmDialogViewModel(Strings.Identity_RestartTitle, Strings.Identity_RestartText, Strings.Identity_QuitNow);
        IdentityMessage = Strings.Identity_RestartText;
        await _dialogs.ShowAsync(restart);
        if (restart.Confirmed)
            _quit?.Invoke();
    }

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

    partial void OnPathVariablesChanged(string value) =>
        Save(s => s with { PathVariables = [.. value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)] });

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

    partial void OnSelectedThemeOptionChanged(Choice<AppTheme> value)
    {
        if (!_loading && value is not null)
            _settings.Update(s => s with { Theme = value.Value });
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
            SelectedThemeOption = ThemeOptions.First(o => o.Value == settings.Theme);
            StartWithSystem = settings.StartWithSystem;
            DeviceName = settings.DeviceName ?? settings.EffectiveDeviceName;
            Port = settings.Port;
            UploadLimitMegabytes = settings.UploadLimitBytesPerSecond / 1_000_000m;
            ParallelTransfers = settings.ParallelTransfers;
            ReceiveFolder = settings.ReceiveFolder ?? _core?.Transfers.DefaultReceiveFolder ?? "";
            HasCustomReceiveFolder = settings.ReceiveFolder is not null;
            VerboseLogging = settings.VerboseLogging;
            PathVariables = string.Join('\n', settings.PathVariables);
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
