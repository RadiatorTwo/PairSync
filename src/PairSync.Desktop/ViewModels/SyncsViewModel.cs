using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PairSync.Application;
using PairSync.Application.Sync;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Domain;

namespace PairSync.Desktop.ViewModels;

/// <summary>A row of the profile table.</summary>
public sealed record SyncProfileRowViewModel(SyncProfileView View, string Name, string Folder, string Device, string Direction, string Status, bool IsHighlighted)
{
    public Guid Id => View.Id;
}

/// <summary>An open conflict under "Conflicts".</summary>
public sealed record SyncConflictRowViewModel(SyncConflict Conflict, string Path, string Cause, IAsyncRelayCommand ResolveCommand);

/// <summary>An entry under "Activity".</summary>
public sealed record SyncActivityRowViewModel(string Text, string When, bool IsProblem);

/// <summary>Sync profiles (screen 03): the table, settings and conflicts of the selected profile.</summary>
public sealed partial class SyncsViewModel : PageViewModel, IDisposable
{
    private readonly PairSyncCore _core;
    private readonly DialogHost _dialogs;
    private readonly IDesktopServices _desktop;
    private readonly TimeProvider _time;
    private readonly ILogger<SyncsViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private int _refreshQueued;
    private bool _loading;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(PauseLabel), nameof(IsActive), nameof(RightsTitle), nameof(ProblemText))]
    private SyncProfileRowViewModel? _selected;

    [ObservableProperty]
    private Choice<SyncDirection>? _selectedDirection;

    [ObservableProperty]
    private Choice<SyncMode>? _selectedMode;

    [ObservableProperty]
    private string _excludes = "";

    [ObservableProperty]
    private bool _allowRead;

    [ObservableProperty]
    private bool _allowWrite;

    [ObservableProperty]
    private bool _allowDelete;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseAllLabel))]
    private bool _allPaused;

    public SyncsViewModel(PairSyncCore core, DialogHost dialogs, IDesktopServices desktop, TimeProvider time) : base(AppPage.Syncs)
    {
        _core = core;
        _dialogs = dialogs;
        _desktop = desktop;
        _time = time;
        _logger = core.Logger<SyncsViewModel>();
        Directions = [new(SyncDirection.TwoWay, Strings.Direction_TwoWay), new(SyncDirection.SendOnly, Strings.Direction_SendOnly),
            new(SyncDirection.ReceiveOnly, Strings.Direction_ReceiveOnly)];
        Modes = [new(SyncMode.Automatic, Strings.Mode_Automatic), new(SyncMode.Manual, Strings.Mode_Manual)];
        _core.Sync.Changed += QueueRefresh;
        _core.Devices.Changed += QueueRefresh;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (Profiles.Any(p => p.View.Status is SyncStatus.Syncing or SyncStatus.Scanning))
                QueueRefresh();
        });
        _timer.Start();
        QueueRefresh();
    }

    public override string Title => Strings.Title_Syncs;

    public IReadOnlyList<Choice<SyncDirection>> Directions { get; }

    public IReadOnlyList<Choice<SyncMode>> Modes { get; }

    public ObservableCollection<SyncProfileRowViewModel> Profiles { get; } = [];

    public ObservableCollection<SyncConflictRowViewModel> Conflicts { get; } = [];

    public ObservableCollection<SyncActivityRowViewModel> Activity { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasSelection => Selected is not null;

    public bool HasConflicts => Conflicts.Count > 0;

    public bool HasActivity => Activity.Count > 0;

    /// <summary>Settings can be changed while the profile is offered or active, not after it was declined or detached.</summary>
    public bool IsActive => Selected?.View.Profile.State is SyncProfileState.Active or SyncProfileState.Offered;

    public string PauseLabel => Selected?.View.Profile.Paused == true ? Strings.Syncs_Resume : Strings.Syncs_Pause;

    public string PauseAllLabel => AllPaused ? Strings.Syncs_ResumeAll : Strings.Syncs_PauseAll;

    public string RightsTitle => string.Format(CultureInfo.CurrentCulture, Strings.Syncs_Rights, Selected?.Device ?? "");

    public string? ProblemText => Selected?.View.Profile.Problem;

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            Ui.Run(() => _ = RefreshAsync());
    }

    public async Task RefreshAsync()
    {
        Volatile.Write(ref _refreshQueued, 0);
        if (_disposed)
            return;
        try
        {
            var views = await _core.Sync.GetProfilesAsync(CancellationToken.None);
            if (_disposed)
                return;
            var selectedId = Selected?.Id;
            Profiles.Clear();
            foreach (var view in views)
                Profiles.Add(Row(view));
            AllPaused = views.Count > 0 && views.Where(v => v.Profile.State == SyncProfileState.Active).All(v => v.Profile.Paused);
            OnPropertyChanged(nameof(HasProfiles));
            // Rows are new objects after every refresh, so this always reloads the details of the selected profile.
            Selected = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        }
        catch (Exception e) when (e is not OutOfMemoryException && !_disposed)
        {
            _logger.LogWarning(e, "Sync profiles could not be read");
        }
    }

    private static SyncProfileRowViewModel Row(SyncProfileView view)
    {
        var culture = CultureInfo.CurrentCulture;
        var profile = view.Profile;
        var direction = profile.Direction switch
        {
            SyncDirection.SendOnly => Strings.Direction_SendOnly,
            SyncDirection.ReceiveOnly => Strings.Direction_ReceiveOnly,
            _ => Strings.Direction_TwoWay,
        };
        var status = view.Status switch
        {
            _ when view.OpenConflicts > 0 && view.Status is SyncStatus.UpToDate =>
                view.OpenConflicts == 1 ? Strings.SyncStatus_ConflictOne : string.Format(culture, Strings.SyncStatus_ConflictMany, view.OpenConflicts),
            SyncStatus.Scanning => Strings.SyncStatus_Scanning,
            SyncStatus.Syncing => string.Format(culture, Strings.SyncStatus_Syncing, Format.Files(view.FilesLeft),
                view.BytesPerSecond > 0 ? Format.Rate(view.BytesPerSecond) : Format.Bytes(view.BytesLeft)),
            SyncStatus.WaitingForDevice => string.Format(culture, Strings.SyncStatus_Waiting, view.PeerName),
            SyncStatus.Paused => Strings.SyncStatus_Paused,
            SyncStatus.OfferPending => string.Format(culture, Strings.SyncStatus_Offer, view.PeerName),
            SyncStatus.Declined => string.Format(culture, Strings.SyncStatus_Declined, view.PeerName),
            SyncStatus.Detached => Strings.SyncStatus_Detached,
            SyncStatus.Problem => profile.Problem ?? "",
            _ => Strings.SyncStatus_UpToDate,
        };
        return new SyncProfileRowViewModel(view, profile.Name, profile.LocalPath, view.PeerName, direction, status,
            view.OpenConflicts > 0 || view.Status == SyncStatus.Problem);
    }

    partial void OnSelectedChanged(SyncProfileRowViewModel? value) => _ = LoadDetailsAsync();

    /// <summary>Settings, conflicts and activity of the selected profile.</summary>
    private async Task LoadDetailsAsync()
    {
        _loading = true;
        try
        {
            if (Selected is not { } row)
            {
                Conflicts.Clear();
                Activity.Clear();
                return;
            }
            var profile = row.View.Profile;
            SelectedDirection = Directions.First(d => d.Value == profile.Direction);
            SelectedMode = Modes.First(m => m.Value == profile.Mode);
            if (Excludes != profile.Excludes)
                Excludes = profile.Excludes;
            AllowRead = profile.AllowRead;
            AllowWrite = profile.AllowWrite;
            AllowDelete = profile.AllowDelete;

            var conflicts = await _core.Sync.GetConflictsAsync(profile.Id, CancellationToken.None);
            var activity = await _core.Sync.GetActivityAsync(profile.Id, 20, CancellationToken.None);
            if (Selected?.Id != profile.Id)
                return;
            Conflicts.Clear();
            foreach (var conflict in conflicts)
                Conflicts.Add(new SyncConflictRowViewModel(conflict, conflict.Path, Strings.Syncs_ConflictCause,
                    new AsyncRelayCommand(() => ResolveAsync(row, conflict))));
            var now = _time.GetUtcNow().UtcDateTime;
            Activity.Clear();
            foreach (var entry in activity)
                Activity.Add(new SyncActivityRowViewModel(entry.Text, Format.Ago(entry.AtUtc, now), entry.Kind is SyncActivityKind.Problem or SyncActivityKind.Conflict));
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(HasActivity));
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSelectedDirectionChanged(Choice<SyncDirection>? value)
    {
        if (value is { } choice)
            Update(p => p.Direction = choice.Value, p => p.Direction == choice.Value);
    }

    partial void OnSelectedModeChanged(Choice<SyncMode>? value)
    {
        if (value is { } choice)
            Update(p => p.Mode = choice.Value, p => p.Mode == choice.Value);
    }

    partial void OnExcludesChanged(string value) => Update(p => p.Excludes = value, p => p.Excludes == value);

    partial void OnAllowReadChanged(bool value) => Update(p => p.AllowRead = value, p => p.AllowRead == value);

    partial void OnAllowWriteChanged(bool value) => Update(p => p.AllowWrite = value, p => p.AllowWrite == value);

    partial void OnAllowDeleteChanged(bool value) => Update(p => p.AllowDelete = value, p => p.AllowDelete == value);

    private void Update(Action<SyncProfile> change, Func<SyncProfile, bool> unchanged)
    {
        if (_loading || Selected is not { } row || unchanged(row.View.Profile))
            return;
        change(row.View.Profile);
        _ = RunAsync(() => _core.Sync.UpdateProfileAsync(row.Id, change, CancellationToken.None));
    }

    [RelayCommand]
    private async Task NewProfileAsync()
    {
        var devices = (await _core.Devices.GetPairedAsync(CancellationToken.None)).Where(d => d.Trust != DeviceTrust.Blocked).ToList();
        var dialog = new NewProfileDialogViewModel(_core, _desktop, devices, Directions, Modes);
        await _dialogs.ShowAsync(dialog);
        if (dialog.Created is { } created)
        {
            await RefreshAsync();
            Selected = Profiles.FirstOrDefault(p => p.Id == created);
        }
    }

    [RelayCommand]
    private Task PauseAllAsync() => RunAsync(() => _core.Sync.SetAllPausedAsync(!AllPaused, CancellationToken.None));

    [RelayCommand]
    private Task TogglePauseAsync() => Selected is { } row
        ? RunAsync(() => _core.Sync.UpdateProfileAsync(row.Id, p => p.Paused = !row.View.Profile.Paused, CancellationToken.None))
        : Task.CompletedTask;

    [RelayCommand]
    private Task SyncNowAsync() => Selected is { } row ? RunAsync(() => _core.Sync.SyncNowAsync(row.Id)) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenFolderAsync() => Selected is { } row ? _desktop.OpenFolderAsync(row.Folder) : Task.CompletedTask;

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is not { } row)
            return;
        var confirm = new ConfirmDialogViewModel(string.Format(CultureInfo.CurrentCulture, Strings.Syncs_RemoveTitle, row.Name),
            Strings.Syncs_RemoveText, Strings.Syncs_Remove);
        await _dialogs.ShowAsync(confirm);
        if (confirm.Confirmed)
            await RunAsync(() => _core.Sync.RemoveProfileAsync(row.Id, CancellationToken.None));
    }

    private async Task ResolveAsync(SyncProfileRowViewModel row, SyncConflict conflict)
    {
        var dialog = new ConflictDialogViewModel(row.Name, row.Device, row.Folder, conflict, _desktop,
            resolution => _core.Sync.ResolveConflictAsync(conflict.Id, resolution, CancellationToken.None));
        await _dialogs.ShowAsync(dialog);
        await LoadDetailsAsync();
    }

    /// <summary>An offer from another device: asks where to put the folder and what the device may do.</summary>
    public async Task ShowOfferAsync(IncomingProfileOffer offer)
    {
        if (!_core.Sync.IncomingOffers.Any(o => o.ProfileId == offer.ProfileId))
            return;
        var dialog = new IncomingOfferDialogViewModel(_core, _desktop, offer, Directions, Modes);
        await _dialogs.ShowAsync(dialog);
        await RefreshAsync();
    }

    private async Task RunAsync(Func<Task> action)
    {
        Error = null;
        try
        {
            await action();
        }
        catch (Exception e) when (e is SyncException or IOException or InvalidOperationException)
        {
            Error = e.Message;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _core.Sync.Changed -= QueueRefresh;
        _core.Devices.Changed -= QueueRefresh;
    }
}

/// <summary>"New sync profile": name, folder, device, direction, mode, excludes.</summary>
public sealed partial class NewProfileDialogViewModel : DialogViewModel
{
    public const string DefaultExcludes = "# Temporary and system files\n*.tmp\n~$*\n.DS_Store\nThumbs.db\ndesktop.ini\n";

    private readonly PairSyncCore _core;
    private readonly IDesktopServices _desktop;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _localPath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private Choice<Guid>? _device;

    [ObservableProperty]
    private Choice<SyncDirection> _direction;

    [ObservableProperty]
    private Choice<SyncMode> _mode;

    [ObservableProperty]
    private string _excludes = DefaultExcludes;

    [ObservableProperty]
    private string? _error;

    public NewProfileDialogViewModel(
        PairSyncCore core, IDesktopServices desktop, IReadOnlyList<PairedDevice> devices, IReadOnlyList<Choice<SyncDirection>> directions,
        IReadOnlyList<Choice<SyncMode>> modes)
    {
        _core = core;
        _desktop = desktop;
        Devices = [.. devices.Select(d => new Choice<Guid>(d.Id, d.Name))];
        Directions = directions;
        Modes = modes;
        _device = Devices.FirstOrDefault();
        _direction = directions[0];
        _mode = modes[0];
    }

    public IReadOnlyList<Choice<Guid>> Devices { get; }

    public IReadOnlyList<Choice<SyncDirection>> Directions { get; }

    public IReadOnlyList<Choice<SyncMode>> Modes { get; }

    public bool HasDevices => Devices.Count > 0;

    /// <summary>The new profile's id once it was created.</summary>
    public Guid? Created { get; private set; }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _desktop.PickFolderAsync(string.IsNullOrWhiteSpace(LocalPath) ? null : LocalPath) is { } folder)
        {
            LocalPath = folder;
            if (string.IsNullOrWhiteSpace(Name))
                Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        }
    }

    private bool CanCreate => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(LocalPath) && Device is not null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Error = null;
        try
        {
            var profile = await _core.Sync.CreateProfileAsync(
                new NewSyncProfile(Name, Device!.Value, LocalPath, Direction.Value, Mode.Value, Excludes), CancellationToken.None);
            Created = profile.Id;
            Close();
        }
        catch (SyncException e)
        {
            Error = e.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => Close();
}

/// <summary>"office-pc wants to sync ‘Projects’": folder, direction, mode and rights, or decline.</summary>
public sealed partial class IncomingOfferDialogViewModel : DialogViewModel
{
    private readonly PairSyncCore _core;
    private readonly IDesktopServices _desktop;
    private readonly IncomingProfileOffer _offer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderNotEmpty))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private string _localPath;

    [ObservableProperty]
    private Choice<SyncDirection> _direction;

    [ObservableProperty]
    private Choice<SyncMode> _mode;

    [ObservableProperty]
    private bool _allowRead = true;

    [ObservableProperty]
    private bool _allowWrite = true;

    [ObservableProperty]
    private bool _allowDelete = true;

    [ObservableProperty]
    private string? _error;

    public IncomingOfferDialogViewModel(
        PairSyncCore core, IDesktopServices desktop, IncomingProfileOffer offer, IReadOnlyList<Choice<SyncDirection>> directions,
        IReadOnlyList<Choice<SyncMode>> modes)
    {
        _core = core;
        _desktop = desktop;
        _offer = offer;
        Directions = directions;
        Modes = modes;
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Offer_Title, offer.DeviceName, offer.Name);
        RightsTitle = string.Format(CultureInfo.CurrentCulture, Strings.Syncs_Rights, offer.DeviceName);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var safeName = string.Concat(offer.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        _localPath = Path.Combine(documents.Length > 0 ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PairSync",
            safeName.Length > 0 ? safeName : "Sync");
        var mirrored = offer.Direction switch
        {
            SyncDirection.SendOnly => SyncDirection.ReceiveOnly,
            SyncDirection.ReceiveOnly => SyncDirection.SendOnly,
            _ => SyncDirection.TwoWay,
        };
        _direction = directions.First(d => d.Value == mirrored);
        _mode = modes[0];
    }

    public string Title { get; }

    public string RightsTitle { get; }

    public IReadOnlyList<Choice<SyncDirection>> Directions { get; }

    public IReadOnlyList<Choice<SyncMode>> Modes { get; }

    public bool FolderNotEmpty
    {
        get
        {
            try
            {
                return Directory.Exists(LocalPath) && Directory.EnumerateFileSystemEntries(LocalPath).Any();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _desktop.PickFolderAsync() is { } folder)
            LocalPath = folder;
    }

    private bool CanAccept => !string.IsNullOrWhiteSpace(LocalPath);

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private async Task AcceptAsync()
    {
        Error = null;
        try
        {
            await _core.Sync.AcceptOfferAsync(_offer.ProfileId,
                new ProfileAcceptance(LocalPath, Direction.Value, Mode.Value, AllowRead, AllowWrite, AllowDelete), CancellationToken.None);
            Close();
        }
        catch (SyncException e)
        {
            Error = e.Message;
        }
    }

    [RelayCommand]
    private async Task DeclineAsync()
    {
        await _core.Sync.DeclineOfferAsync(_offer.ProfileId);
        Close();
    }
}

/// <summary>A side of the conflict dialog: device, size, edit time (for information only) and a short hash.</summary>
public sealed record ConflictSide(string Device, string Size, string Edited, string Hash);

/// <summary>"docs/plan.md was changed on both devices" with the three ways out.</summary>
public sealed partial class ConflictDialogViewModel : DialogViewModel
{
    private readonly Func<ConflictResolution, Task> _resolve;
    private readonly IDesktopServices _desktop;
    private readonly string _folder;

    [ObservableProperty]
    private string? _error;

    public ConflictDialogViewModel(
        string profileName, string peerName, string folder, SyncConflict conflict, IDesktopServices desktop, Func<ConflictResolution, Task> resolve)
    {
        _resolve = resolve;
        _desktop = desktop;
        _folder = Path.GetDirectoryName(Path.Combine(folder, conflict.Path.Replace('/', Path.DirectorySeparatorChar))) ?? folder;
        var culture = CultureInfo.CurrentCulture;
        Rubric = string.Format(culture, Strings.Conflict_Rubric, profileName);
        Title = string.Format(culture, Strings.Conflict_Title, conflict.Path);
        KeepOtherLabel = string.Format(culture, Strings.Conflict_KeepOther, peerName);
        Local = Side(Strings.Conflict_ThisDevice, conflict.LocalSize, conflict.LocalMTimeUtc, conflict.LocalSha256);
        Remote = Side(peerName, conflict.RemoteSize, conflict.RemoteMTimeUtc, conflict.RemoteSha256);
    }

    private static ConflictSide Side(string device, long size, DateTime edited, byte[]? sha) => new(device, Format.Bytes(size),
        string.Format(CultureInfo.CurrentCulture, Strings.Conflict_Edited, edited.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
        sha is null ? "" : "SHA-256 " + Convert.ToHexStringLower(sha)[..12] + "…");

    public string Rubric { get; }

    public string Title { get; }

    public string KeepOtherLabel { get; }

    public ConflictSide Local { get; }

    public ConflictSide Remote { get; }

    [RelayCommand]
    private Task ShowFolderAsync() => _desktop.OpenFolderAsync(_folder);

    [RelayCommand]
    private Task KeepThisAsync() => ResolveAsync(ConflictResolution.KeepThisDevice);

    [RelayCommand]
    private Task KeepOtherAsync() => ResolveAsync(ConflictResolution.KeepOtherDevice);

    [RelayCommand]
    private Task KeepBothAsync() => ResolveAsync(ConflictResolution.KeepBoth);

    [RelayCommand]
    private void Cancel() => Close();

    private async Task ResolveAsync(ConflictResolution resolution)
    {
        Error = null;
        try
        {
            await _resolve(resolution);
            Close();
        }
        catch (SyncException e)
        {
            Error = e.Message;
        }
    }
}
