using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PairSync.Application;
using PairSync.Application.Claude;
using PairSync.Desktop.Platform;
using PairSync.Desktop.Resources;
using PairSync.Domain;
using PairSync.Protocol;

namespace PairSync.Desktop.ViewModels;

/// <summary>A row under "Portable config": a checkbox and the diff status.</summary>
public sealed partial class PortableItemRowViewModel(PortableEntry entry, bool enabled) : ObservableObject
{
    public PortableEntry Entry { get; } = entry;

    public string Key => Entry.Item.Key;

    public string Title => Entry.Item.Title;

    [ObservableProperty]
    private bool _enabled = enabled;

    /// <summary>"3 keys", "+2 · 12 same", "same", "not here".</summary>
    public string Status { get; } = DiffStatus(entry);

    public bool HasChanges => Entry.HasChanges;

    private static string DiffStatus(PortableEntry e)
    {
        if (e.Item.Key == PortableItem.SettingsKey)
            return (e.Added + e.Changed) switch
            {
                0 => Strings.Claude_Same,
                1 => Strings.Claude_OneKey,
                var n => string.Format(CultureInfo.CurrentCulture, Strings.Claude_Keys, n),
            };
        if (e.Added + e.Changed + e.Same == 0)
            return Strings.Claude_NotHere;
        var parts = new List<string>();
        if (e.Added > 0)
            parts.Add($"+{e.Added}");
        if (e.Changed > 0)
            parts.Add($"~{e.Changed}");
        if (parts.Count == 0)
            return Strings.Claude_Same;
        if (e.Same > 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Claude_NSame, e.Same));
        return string.Join(" · ", parts);
    }
}

/// <summary>A planned CLI command under "Plugins &amp; MCP", or a skipped <c>@synced</c> plugin.</summary>
public sealed record ClaudeCommandRowViewModel(string Command, string Meta, bool Skipped);

/// <summary>An absolute path the user maps for the target.</summary>
public sealed partial class PathMappingRowViewModel(string original) : ObservableObject
{
    public string Original { get; } = original;

    [ObservableProperty]
    private string _mapping = original;
}

/// <summary>An entry under "Runs programs · confirm each"; unchecked by default.</summary>
public sealed partial class ExecutableRowViewModel(ExecutableItem item, bool allowed, Action changed) : ObservableObject
{
    public ExecutableItem Item { get; } = item;

    public string Title => Item.Title;

    public string Command => Item.Command;

    /// <summary>False if the target does not allow installing programs.</summary>
    public bool Allowed { get; } = allowed;

    [ObservableProperty]
    private bool _confirmed;

    partial void OnConfirmedChanged(bool value) => changed();
}

/// <summary>The result of one step after "Apply".</summary>

/// <summary>Claude Code (screen 04): compare this device with a paired one, confirm what runs programs, apply there.</summary>
public sealed partial class ClaudeCodeViewModel : PageViewModel, IDisposable
{
    private readonly PairSyncCore _core;
    private readonly DialogHost _dialogs;
    private readonly ILogger<ClaudeCodeViewModel> _logger;
    private CancellationTokenSource? _loading;
    private ClaudePlan? _plan;
    private ClaudeTarget? _target;
    private bool _disposed;
    private bool _keepPlan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    private Choice<PairedDevice>? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ShowDiffCommand))]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _heading = Strings.Nav_ClaudeCode;

    [ObservableProperty]
    private string _subline = "";

    [ObservableProperty]
    private string _changeCount = "";

    [ObservableProperty]
    private string _programCount = "";

    [ObservableProperty]
    private string _confirmedText = "";

    [ObservableProperty]
    private string _applyLabel = Strings.Claude_Apply;

    [ObservableProperty]
    private string? _programsHint;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _pendingText;

    /// <summary>Claude Code is not installed on this device: the page offers to install it.</summary>
    [ObservableProperty]
    private bool _localMissing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallLocalCommand), nameof(InstallToolsCommand))]
    private bool _isInstalling;

    /// <summary>"Missing on this device for Claude Code plugins: git, jq"; null if nothing is missing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolsMissing))]
    private string? _missingToolsText;

    public bool ToolsMissing => MissingToolsText is not null;

    public ClaudeCodeViewModel(PairSyncCore core, DialogHost dialogs, IDesktopServices? desktop = null) : base(AppPage.ClaudeCode)
    {
        _core = core;
        _dialogs = dialogs;
        Log = new ClaudeLogViewModel(desktop);
        Log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudeLogViewModel.HasEntries))
                OnPropertyChanged(nameof(HasResults));
        };
        _core.Claude.Incoming += OnIncoming;
        _logger = core.Logger<ClaudeCodeViewModel>();
        _core.Devices.Changed += OnDevicesChanged;
        _core.Claude.Changed += OnPendingChanged;
        UpdatePending();
    }

    public ObservableCollection<Choice<PairedDevice>> Devices { get; } = [];

    public ObservableCollection<PortableItemRowViewModel> PortableItems { get; } = [];

    public ObservableCollection<ClaudeCommandRowViewModel> Commands { get; } = [];

    public ObservableCollection<string> McpNotes { get; } = [];

    public ObservableCollection<PathMappingRowViewModel> PathMappings { get; } = [];

    public ObservableCollection<ExecutableRowViewModel> Executables { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>What "Apply", the installers or another device's apply did here, step by step.</summary>
    public ClaudeLogViewModel Log { get; }

    /// <summary>"laptop applies its Claude Code configuration here" while another device's apply runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReceiving))]
    private string? _incomingText;

    public bool IsReceiving => IncomingText is not null;

    public bool HasDevice => SelectedDevice is not null;

    public bool HasDevices => Devices.Count > 0;

    public bool HasPlan => IsLoaded;

    public bool HasCommands => Commands.Count > 0;

    public bool HasMappings => PathMappings.Count > 0;

    public bool HasExecutables => Executables.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    public bool HasResults => Log.HasEntries || ResultText is not null;

    public bool HasPending => PendingText is not null;

    /// <summary>The area below the columns: results of the last apply and notes.</summary>
    public bool HasFooter => HasResults || HasNotes;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(HasResults) or nameof(HasNotes))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasFooter)));
    }

    public override void OnOpened() => _ = LoadDevicesAsync();

    private void OnDevicesChanged() => Ui.Run(() => _ = LoadDevicesAsync());

    private void OnPendingChanged() => Ui.Run(UpdatePending);

    private void UpdatePending()
    {
        LocalMissing = !_core.Claude.IsInstalledLocally;
        var tools = _core.Claude.MissingToolsLocally;
        MissingToolsText = tools.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.Claude_ToolsMissing, string.Join(", ", tools));
        var pending = _core.Claude.Pending;
        PendingText = pending is null
            ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.Claude_Pending, pending.Steps.Count, pending.DeviceName);
        OnPropertyChanged(nameof(HasPending));
    }

    public async Task LoadDevicesAsync()
    {
        try
        {
            var devices = (await _core.Devices.GetPairedAsync(CancellationToken.None)).Where(d => d.Trust == DeviceTrust.Active).ToList();
            if (_disposed)
                return;
            var selected = SelectedDevice?.Value.Id;
            Devices.Clear();
            foreach (var device in devices)
                Devices.Add(new Choice<PairedDevice>(device, device.Name));
            OnPropertyChanged(nameof(HasDevices));
            // The same device again (renamed, permissions changed) keeps the loaded plan.
            _keepPlan = Devices.Any(d => d.Value.Id == selected);
            SelectedDevice = Devices.FirstOrDefault(d => d.Value.Id == selected) ?? Devices.FirstOrDefault();
            _keepPlan = false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Loading devices failed");
        }
    }

    partial void OnSelectedDeviceChanged(Choice<PairedDevice>? value)
    {
        if (_keepPlan)
            return;
        IsLoaded = false;
        if (!IsReceiving)
        {
            Log.Start("");
            ResultText = null;
        }
        OnPropertyChanged(nameof(HasResults));
        if (value is not null)
            _ = CompareAsync();
    }

    /// <summary>Asks the target for its state and builds the plan.</summary>
    [RelayCommand]
    public async Task CompareAsync()
    {
        if (SelectedDevice?.Value is not { } device)
            return;
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        IsBusy = true;
        Error = null;
        IsLoaded = false;
        try
        {
            var local = await _core.Claude.ReadLocalAsync(loading.Token);
            var target = await _core.Claude.FetchTargetAsync(device, loading.Token);
            if (loading.IsCancellationRequested)
                return;
            Show(local, target, ClaudePlanner.Plan(local, target.Snapshot, target.CanInstallClaude));
        }
        catch (ClaudeUnavailableException e)
        {
            if (!loading.IsCancellationRequested)
                Error = e.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Claude Code comparison failed");
            Error = e.Message;
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
                IsBusy = false;
        }
    }

    private void Show(ClaudeSnapshot local, ClaudeTarget target, ClaudePlan plan)
    {
        _plan = plan;
        _target = target;
        var name = target.Device.Name;
        Heading = string.Format(CultureInfo.CurrentCulture, Strings.Claude_Heading, name);
        var source = Folder(local.Environment);
        var there = Folder(target.Snapshot.Environment);
        var version = target.Snapshot.Version is { } v
            ? string.Format(CultureInfo.CurrentCulture, Strings.Claude_Version, v)
            : Strings.Claude_NotInstalled;
        Subline = $"{source} → {there} · {version}";
        ApplyLabel = string.Format(CultureInfo.CurrentCulture, Strings.Claude_ApplyOn, name);
        ProgramsHint = target.AllowPrograms ? null : string.Format(CultureInfo.CurrentCulture, Strings.Claude_NoPrograms, name);

        PortableItems.Clear();
        foreach (var entry in plan.Entries)
            PortableItems.Add(new PortableItemRowViewModel(entry, entry.Item.DefaultOn));

        Commands.Clear();
        foreach (var step in plan.Steps)
            Commands.Add(new ClaudeCommandRowViewModel(ClaudeStepText.Of(step), step.Meta, false));
        foreach (var plugin in plan.SyncedPlugins)
            Commands.Add(new ClaudeCommandRowViewModel($"install {plugin}", Strings.Claude_SyncedSkipped, true));

        McpNotes.Clear();
        foreach (var server in plan.McpServers)
        {
            if (server.SecretVariables.Count > 0)
                McpNotes.Add(string.Format(CultureInfo.CurrentCulture, Strings.Claude_McpSecrets, server.Name, string.Join(", ", server.SecretVariables)));
        }

        PathMappings.Clear();
        foreach (var path in plan.Executables.SelectMany(e => e.UnmappedPaths).Distinct(StringComparer.Ordinal))
            PathMappings.Add(new PathMappingRowViewModel(path));

        Executables.Clear();
        foreach (var item in plan.Executables)
            Executables.Add(new ExecutableRowViewModel(item, target.AllowPrograms, UpdateCounts));

        Notes.Clear();
        foreach (var note in plan.Notes)
            Notes.Add(note);

        ChangeCount = string.Format(CultureInfo.CurrentCulture, Strings.Claude_ItemsToChange,
            plan.Entries.Where(e => e.Item.Key != PortableItem.SettingsKey).Sum(e => e.Added + e.Changed) + plan.Settings.Count + plan.Steps.Count);
        ProgramCount = string.Format(CultureInfo.CurrentCulture, Strings.Claude_RunPrograms, plan.Executables.Count);
        UpdateCounts();
        OnPropertyChanged(nameof(HasCommands));
        OnPropertyChanged(nameof(HasMappings));
        OnPropertyChanged(nameof(HasExecutables));
        OnPropertyChanged(nameof(HasNotes));
        IsLoaded = true;
    }

    /// <summary>"~/.claude", or the full path plus "(CLAUDE_CONFIG_DIR)" if the variable chose it.</summary>
    private static string Folder(ClaudeEnvironment environment)
    {
        var folder = environment.ConfigDir;
        var home = environment.HomeDir.TrimEnd('/', '\\');
        if (home.Length > 0 && folder.StartsWith(home, StringComparison.OrdinalIgnoreCase) && folder.Length > home.Length && folder[home.Length] is '/' or '\\')
            folder = "~" + folder[home.Length..].Replace('\\', '/');
        return environment.ConfigDirFromEnv ? $"{folder} (CLAUDE_CONFIG_DIR)" : folder;
    }

    private void UpdateCounts() =>
        ConfirmedText = string.Format(CultureInfo.CurrentCulture, Strings.Claude_Confirmed, Executables.Count(e => e.Confirmed), Executables.Count);

    private ClaudeSelection Selection() => new(
        PortableItems.Where(p => p.Enabled).Select(p => p.Key).ToHashSet(StringComparer.Ordinal),
        Executables.Where(e => e.Confirmed && e.Allowed).Select(e => e.Item.Id).ToHashSet(StringComparer.Ordinal),
        PathMappings.Where(m => !string.IsNullOrWhiteSpace(m.Mapping) && m.Mapping != m.Original)
            .ToDictionary(m => m.Original, m => m.Mapping.Trim(), StringComparer.Ordinal));

    private bool CanApply() => IsLoaded && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_plan is null || _target is null)
            return;
        var request = ClaudePlanner.BuildApply(_plan, Selection());
        var titles = request.Steps.Select(ClaudeStepText.Of).ToList();
        IsBusy = true;
        Error = null;
        Log.Start(ApplyLabel);
        Log.Add(string.Format(CultureInfo.CurrentCulture, Strings.Claude_LogSending, request.Files.Count, request.Settings.Count, request.Steps.Count));
        ResultText = Strings.Claude_Applying;
        OnPropertyChanged(nameof(HasResults));
        try
        {
            var outcome = await _core.Claude.ApplyAsync(_target.Device, request,
                result => Ui.Run(() => AddStep(titles, result)), CancellationToken.None);
            ResultText = outcome.Error is { } error
                ? error
                : string.Format(CultureInfo.CurrentCulture, Strings.Claude_Applied, outcome.FilesWritten, outcome.SettingsWritten)
                  + (outcome.BackupFolder is null ? "" : " " + string.Format(CultureInfo.CurrentCulture, Strings.Claude_Backup, outcome.BackupFolder));
            Log.Add(ResultText!, outcome.Error is null ? ClaudeLogKind.Done : ClaudeLogKind.Failed);
            foreach (var note in outcome.Notes)
                Notes.Add(note);
            OnPropertyChanged(nameof(HasNotes));
        }
        catch (ClaudeUnavailableException e)
        {
            ResultText = e.Message;
            Log.Add(e.Message, ClaudeLogKind.Failed);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasResults));
        }
        // Show the new state of the target; the log stays.
        var text = ResultText;
        await CompareAsync();
        ResultText = text;
        OnPropertyChanged(nameof(HasResults));
    }

    private void AddStep(IReadOnlyList<string> titles, ClaudeStepResult result) =>
        AddStep(result.Index >= 0 && result.Index < titles.Count ? titles[result.Index] : $"#{result.Index + 1}", result.Status, result.ExitCode, result.Output);

    private void AddStep(string title, ClaudeStepStatus status, int exitCode, string? output)
    {
        var (kind, text) = status switch
        {
            ClaudeStepStatus.Done => (ClaudeLogKind.Done, Strings.Claude_StepDone),
            ClaudeStepStatus.Skipped => (ClaudeLogKind.Skipped, Strings.Claude_StepSkipped),
            ClaudeStepStatus.Pending => (ClaudeLogKind.Pending, Strings.Claude_StepPending),
            _ => (ClaudeLogKind.Failed, string.Format(CultureInfo.CurrentCulture, Strings.Claude_StepFailed, exitCode)),
        };
        Log.Add(title, kind, text, output);
    }

    /// <summary>Another device applies its configuration here: the log shows each step as it happens.</summary>
    private void OnIncoming(ClaudeIncomingEvent e) => Ui.Run(() =>
    {
        switch (e.Stage)
        {
            case ClaudeIncomingStage.Started:
                IncomingText = string.Format(CultureInfo.CurrentCulture, Strings.Claude_Receiving, e.DeviceName);
                Log.Start(string.Format(CultureInfo.CurrentCulture, Strings.Claude_LogFrom, e.DeviceName));
                ResultText = null;
                Log.Add(e.Title);
                break;
            case ClaudeIncomingStage.Info:
                Log.Add(e.Title, e.Title.StartsWith("Running", StringComparison.Ordinal) ? ClaudeLogKind.Running : ClaudeLogKind.Info);
                break;
            case ClaudeIncomingStage.Step:
                AddStep(e.Title, e.Status ?? ClaudeStepStatus.Done, e.ExitCode, e.Output);
                break;
            case ClaudeIncomingStage.Finished or ClaudeIncomingStage.Failed:
                if (!IsReceiving)
                    Log.Start(string.Format(CultureInfo.CurrentCulture, Strings.Claude_LogFrom, e.DeviceName));
                Log.Add(e.Title, e.Stage == ClaudeIncomingStage.Finished ? ClaudeLogKind.Done : ClaudeLogKind.Failed);
                ResultText = e.Title;
                IncomingText = null;
                UpdatePending();
                break;
        }
        OnPropertyChanged(nameof(HasResults));
    });

    partial void OnIsBusyChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(IsLoaded))]
    private Task ShowDiffAsync()
    {
        if (_plan is null)
            return Task.CompletedTask;
        var lines = new List<string>();
        foreach (var entry in _plan.Entries.Where(e => e.Item.Key != PortableItem.SettingsKey))
        {
            foreach (var file in entry.ToSend)
                lines.Add($"{(_target!.Snapshot.Files.Any(f => f.Path == file.Path) ? "~" : "+")} {file.Path}");
        }
        foreach (var change in _plan.Settings)
            lines.Add($"~ settings.json: {change.Key}{(change.SubKey is null ? "" : "." + change.SubKey)}{(change.ExecutableId is null ? "" : " ▸")}");
        foreach (var step in _plan.Steps)
            lines.Add($"$ claude {ClaudeStepText.Of(step)}");
        if (lines.Count == 0)
            lines.Add(Strings.Claude_NothingToChange);
        return _dialogs.ShowAsync(new ClaudeDiffDialogViewModel(Heading, lines));
    }

    [RelayCommand]
    private async Task ApplyPendingAsync()
    {
        Log.Start(Strings.Claude_ApplyPending);
        ResultText = Strings.Claude_Applying;
        OnPropertyChanged(nameof(HasResults));
        var titles = _core.Claude.Pending?.Steps.Select(ClaudeStepText.Of).ToList() ?? [];
        try
        {
            await _core.Claude.ApplyPendingAsync(result => Ui.Run(() => AddStep(titles, result)), CancellationToken.None);
            ResultText = Strings.Claude_PendingApplied;
            Log.Add(ResultText, ClaudeLogKind.Done);
        }
        catch (ClaudeUnavailableException e)
        {
            ResultText = e.Message;
            Log.Add(e.Message, ClaudeLogKind.Failed);
        }
        OnPropertyChanged(nameof(HasResults));
    }

    [RelayCommand]
    private void DiscardPending() => _core.Claude.DiscardPending();

    private bool CanInstallLocal() => !IsInstalling;

    /// <summary>"Install missing tools": git, bun and jq for plugins, with winget or the package manager.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallLocal))]
    private async Task InstallToolsAsync()
    {
        IsInstalling = true;
        var missing = _core.Claude.MissingToolsLocally;
        Log.Start(Strings.Claude_InstallTools);
        foreach (var tool in missing)
            Log.Add(string.Format(CultureInfo.CurrentCulture, Strings.Claude_ToolInstall, tool), ClaudeLogKind.Running);
        ResultText = Strings.Claude_ToolsInstalling;
        OnPropertyChanged(nameof(HasResults));
        try
        {
            var results = await _core.Claude.InstallToolsLocalAsync(missing, CancellationToken.None);
            foreach (var result in results)
                AddStep(string.Format(CultureInfo.CurrentCulture, Strings.Claude_ToolInstall, result.Tool),
                    result.Success ? ClaudeStepStatus.Done : ClaudeStepStatus.Failed, 1,
                    string.Join("\n", new[] { result.Error, result.Output }.Where(t => !string.IsNullOrWhiteSpace(t))));
            ResultText = results.All(r => r.Success) ? Strings.Claude_ToolsInstalled : Strings.Claude_ToolsPartly;
            Log.Add(ResultText, results.All(r => r.Success) ? ClaudeLogKind.Done : ClaudeLogKind.Failed);
        }
        finally
        {
            IsInstalling = false;
            UpdatePending();
            OnPropertyChanged(nameof(HasResults));
        }
    }

    /// <summary>"Install Claude Code" on this device: official installer, then ~/.local/bin on PATH.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallLocal))]
    private async Task InstallLocalAsync()
    {
        IsInstalling = true;
        Log.Start(Strings.Claude_Install);
        Log.Add(Strings.Claude_Installing, ClaudeLogKind.Running);
        ResultText = Strings.Claude_Installing;
        OnPropertyChanged(nameof(HasResults));
        try
        {
            var result = await _core.Claude.InstallLocalAsync(CancellationToken.None);
            ResultText = result.Success
                ? string.Format(CultureInfo.CurrentCulture, Strings.Claude_Installed, result.Version ?? "?") + " " + ClaudeService.DescribePath(result.Path)
                : result.Error;
            AddStep(Strings.Claude_Install, result.Success ? ClaudeStepStatus.Done : ClaudeStepStatus.Failed, 1, result.Output);
            Log.Add(ResultText ?? "", result.Success ? ClaudeLogKind.Done : ClaudeLogKind.Failed);
        }
        finally
        {
            IsInstalling = false;
            UpdatePending();
            OnPropertyChanged(nameof(HasResults));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _loading?.Cancel();
        _core.Devices.Changed -= OnDevicesChanged;
        _core.Claude.Incoming -= OnIncoming;
        _core.Claude.Changed -= OnPendingChanged;
    }
}

/// <summary>"Show full diff": every file, settings key and command the apply would change.</summary>
public sealed partial class ClaudeDiffDialogViewModel(string title, IReadOnlyList<string> lines) : DialogViewModel
{
    public string Title { get; } = title;

    public IReadOnlyList<string> Lines { get; } = lines;

    [RelayCommand]
    private void Done() => Close();
}
