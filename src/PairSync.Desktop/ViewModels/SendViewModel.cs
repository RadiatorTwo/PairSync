using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PairSync.Application;
using PairSync.Application.Connections;
using PairSync.Application.Presence;
using PairSync.Application.Transfers;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Domain;

namespace PairSync.Desktop.ViewModels;

/// <summary>A row of the Send table.</summary>
public sealed class SendEntryViewModel
{
    private readonly bool _pending;

    public SendEntryViewModel(string path, DraftEntry? entry, bool pending, Action<SendEntryViewModel> remove)
    {
        Path = path;
        Entry = entry;
        _pending = pending;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }

    public DraftEntry? Entry { get; }

    public string Name => Entry?.Name ?? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));

    /// <summary>Folders end with a slash, as in the mockup ("Photos/").</summary>
    public string Display => Entry is { IsFolder: true } ? Name + "/" : Name;

    public string Size => Entry is not null ? Format.Bytes(Entry.Size) : _pending ? "…" : "—";

    public string Files => Entry is not null ? Format.Count(Entry.FileCount) : _pending ? "…" : "—";

    public IRelayCommand RemoveCommand { get; }
}

/// <summary>A paired device in the "Send to" segments; offline ones are disabled (mockup).</summary>
/// <param name="ViaInternet">Reachable only over an internet link (slower than the LAN).</param>
public sealed record SendTarget(Guid Id, string Name, bool IsOnline, bool ViaInternet = false)
{
    public override string ToString() => IsOnline ? Name : string.Format(CultureInfo.CurrentCulture, Strings.Send_Offline, Name);
}

/// <summary>Send files once (screen 02).</summary>
public sealed partial class SendViewModel : PageViewModel, IDisposable
{
    private readonly PairSyncCore _core;
    private readonly IDesktopServices _desktop;
    private readonly Action<AppPage> _navigate;
    private readonly List<string> _paths = [];
    private int _scanVersion;
    private SendDraft? _draft;
    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SendLabel), nameof(RouteText))]
    private SendTarget? _target;

    [ObservableProperty]
    private string _subfolder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeepBoth), nameof(IsReplace), nameof(IsSkip))]
    private ExistingFilePolicy _policy = ExistingFilePolicy.KeepBoth;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string _itemsSummary = "";

    [ObservableProperty]
    private string _sizeSummary = "";

    /// <summary>Skipped links and names another system cannot store.</summary>
    [ObservableProperty]
    private string? _problems;

    [ObservableProperty]
    private string? _error;

    public SendViewModel(PairSyncCore core, IDesktopServices desktop, Action<AppPage> navigate) : base(AppPage.Send)
    {
        _core = core;
        _desktop = desktop;
        _navigate = navigate;
        _core.Presence.Changed += OnPresenceChanged;
        _core.Internet.Changed += OnPresenceChanged;
        LoadTargets();
        UpdateSummary();
    }

    public override string Title => Strings.Title_Send;

    public ObservableCollection<SendEntryViewModel> Entries { get; } = [];

    public ObservableCollection<SendTarget> Targets { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    public bool HasTargets => Targets.Count > 0;

    /// <summary>Where the receiver is offered to save: its downloads folder, below this device's name.</summary>
    public string DestinationPrefix => $"Downloads/PairSync/{_core.Settings.Current.EffectiveDeviceName}/";

    public bool IsKeepBoth
    {
        get => Policy == ExistingFilePolicy.KeepBoth;
        set { if (value) Policy = ExistingFilePolicy.KeepBoth; }
    }

    public bool IsReplace
    {
        get => Policy == ExistingFilePolicy.Replace;
        set { if (value) Policy = ExistingFilePolicy.Replace; }
    }

    public bool IsSkip
    {
        get => Policy == ExistingFilePolicy.Skip;
        set { if (value) Policy = ExistingFilePolicy.Skip; }
    }

    public string SendLabel => Target is null ? Strings.Send_ButtonNoTarget : string.Format(CultureInfo.CurrentCulture, Strings.Send_Button, Target.Name);

    public string RouteText => Target switch
    {
        { IsOnline: false } => string.Format(CultureInfo.CurrentCulture, Strings.Send_RouteOffline, Target.Name),
        { ViaInternet: true } => Strings.Send_RouteInternet,
        _ => Strings.Send_Route,
    };

    /// <summary>Adds dropped or picked files and folders; the list is scanned again in the background.</summary>
    public Task AddPathsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(p => System.IO.Path.GetFullPath(p)))
        {
            if (!_paths.Contains(path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                _paths.Add(path);
        }
        return ScanAsync();
    }

    [RelayCommand]
    private async Task BrowseFilesAsync() => await AddPathsAsync(await _desktop.PickFilesAsync());

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        if (await _desktop.PickFolderAsync() is { } folder)
            await AddPathsAsync([folder]);
    }

    private void Remove(SendEntryViewModel entry)
    {
        _paths.Remove(entry.Path);
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        var version = ++_scanVersion;
        Error = null;
        if (_paths.Count == 0)
        {
            ShowDraft(null);
            return;
        }
        IsScanning = true;
        ShowPending();
        var paths = _paths.ToList();
        SendDraft? draft = null;
        string? error = null;
        try
        {
            draft = await Task.Run(() => SendScanner.Scan(paths));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = e.Message;
        }
        if (version != _scanVersion)
            return; // the list changed meanwhile; a newer scan shows its result
        IsScanning = false;
        Error = error;
        ShowDraft(draft);
    }

    /// <summary>Rows for the paths while they are read.</summary>
    private void ShowPending()
    {
        Entries.Clear();
        foreach (var path in _paths)
            Entries.Add(new SendEntryViewModel(path, null, pending: true, Remove));
        OnPropertyChanged(nameof(HasEntries));
    }

    private void ShowDraft(SendDraft? draft)
    {
        _draft = draft;
        Entries.Clear();
        if (draft is not null)
        {
            // Paths that were not found or cannot be sent keep a row, so they can be removed.
            foreach (var path in _paths)
                Entries.Add(new SendEntryViewModel(path, draft.Entries.FirstOrDefault(e => e.SourcePath == path), pending: false, Remove));
        }
        var notes = new List<string>();
        if (draft is { SkippedLinks.Count: > 0 })
            notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.Send_Skipped, draft.SkippedLinks.Count));
        if (draft is not null)
            notes.AddRange(draft.Problems);
        Problems = notes.Count > 0 ? string.Join(Environment.NewLine, notes) : null;
        OnPropertyChanged(nameof(HasEntries));
        UpdateSummary();
        SendCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSummary()
    {
        var count = _draft?.Entries.Count ?? 0;
        ItemsSummary = $"{Format.Items(count)} · {Format.Files(_draft?.FileCount ?? 0)}";
        SizeSummary = Format.Bytes(_draft?.TotalBytes ?? 0);
    }

    private bool CanSend() => Target is not null && !IsScanning && _draft is { Items.Count: > 0, Problems.Count: 0 };

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (Target is not { } target || _draft is not { } draft)
            return;
        try
        {
            var folder = string.IsNullOrWhiteSpace(Subfolder) ? null : Subfolder.Trim();
            await _core.Transfers.SendAsync(target.Id, draft, Policy, folder, CancellationToken.None);
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
        {
            Error = string.Format(CultureInfo.CurrentCulture, Strings.Send_Failed, e.Message);
            return;
        }
        _paths.Clear();
        Subfolder = "";
        ShowDraft(null);
        _navigate(AppPage.Overview);
    }

    private void OnPresenceChanged() => Ui.Run(() =>
    {
        if (!_disposed)
            LoadTargets();
    });

    /// <summary>Paired, not blocked devices; keeps the selection when the device is still there.</summary>
    private void LoadTargets()
    {
        var selected = Target?.Id;
        var targets = _core.Presence.Devices
            .Where(d => d.IsPaired && d.Trust != DeviceTrust.Blocked)
            .Select(d => (Device: d, Route: _core.Links.RouteTo(d.Id)))
            .Select(x => new SendTarget(x.Device.Id, x.Device.Name, x.Route != PeerRoute.None, x.Route == PeerRoute.Internet))
            .ToList();
        if (targets.SequenceEqual(Targets))
            return;
        Targets.Clear();
        foreach (var target in targets)
            Targets.Add(target);
        Target = targets.FirstOrDefault(t => t.Id == selected) ?? targets.FirstOrDefault(t => t.IsOnline);
        OnPropertyChanged(nameof(HasTargets));
    }

    public void Dispose()
    {
        _disposed = true;
        _core.Presence.Changed -= OnPresenceChanged;
        _core.Internet.Changed -= OnPresenceChanged;
    }
}
