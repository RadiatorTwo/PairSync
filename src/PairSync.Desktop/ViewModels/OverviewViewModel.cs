using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PairSync.Application;
using PairSync.Application.Internet;
using PairSync.Application.Presence;
using PairSync.Application.Transfers;
using PairSync.Desktop.Resources;
using PairSync.Domain;

namespace PairSync.Desktop.ViewModels;

public enum CardKind
{
    Connected,
    Offline,
    Blocked,
    Found,
}

/// <summary>A button on a device card ("Connect via internet…", "Paste answer…", "Cancel").</summary>
public sealed record CardActionViewModel(string Label, IAsyncRelayCommand Command, bool IsPrimary = false)
{
    public CardActionViewModel(string label, Func<Task> action, bool isPrimary = false)
        : this(label, new AsyncRelayCommand(action), isPrimary)
    {
    }
}

/// <summary>One card of the device grid.</summary>
public sealed class DeviceCardViewModel(
    NearbyDevice device, CardKind kind, string? state, string line1, string? line2, Func<NearbyDevice, Task>? pair,
    string? internetText = null, IReadOnlyList<CardActionViewModel>? actions = null)
{
    public Guid Id => Device.Id;

    public NearbyDevice Device { get; } = device;

    public string Name => Device.Name;

    public CardKind Kind { get; } = kind;

    public bool IsConnected => Kind == CardKind.Connected;

    public bool IsFound => Kind == CardKind.Found;

    /// <summary>"Connected", "Offline", "Blocked"; null for found devices.</summary>
    public string? State { get; } = state;

    public string Line1 { get; } = line1;

    public string? Line2 { get; } = line2;

    /// <summary>State of an internet connection in progress: "Waiting for answer · expires in 08:41", "Connection lost · new code needed".</summary>
    public string? InternetText { get; } = internetText;

    public IReadOnlyList<CardActionViewModel> Actions { get; } = actions ?? [];

    public bool HasActions => Actions.Count > 0;

    public IAsyncRelayCommand PairCommand { get; } = new AsyncRelayCommand(() => pair?.Invoke(device) ?? Task.CompletedTask);
}

/// <summary>The NAT banner over the device grid: "Can't connect directly to office-pc." and why.</summary>
public sealed record NatBannerViewModel(Guid DeviceId, string Headline, string Detail);

/// <summary>A job under "Active transfers"; updated in place so the list does not flicker.</summary>
public sealed partial class TransferRowViewModel(Guid id, TransferService transfers) : ObservableObject
{
    public Guid Id { get; } = id;

    [ObservableProperty]
    private string _title = "";

    /// <summary>"→ laptop-win11" or "← laptop-win11".</summary>
    [ObservableProperty]
    private string _peer = "";

    [ObservableProperty]
    private string _size = "";

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private bool _isResumed;

    [ObservableProperty]
    private string _meta = "";

    [ObservableProperty]
    private string? _rate;

    [ObservableProperty]
    private string? _resumedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume))]
    private JobState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    private bool _pausedByPeer;

    public bool CanPause => State is JobState.Running or JobState.Waiting or JobState.AwaitingAcceptance;

    /// <summary>A pause by the other device ends there, not here.</summary>
    public bool CanResume => State == JobState.Paused && !PausedByPeer;

    [RelayCommand]
    private Task Pause() => transfers.PauseAsync(Id);

    [RelayCommand]
    private Task Resume() => transfers.ResumeAsync(Id);

    [RelayCommand]
    private Task Cancel() => transfers.CancelAsync(Id);

    public void Update(JobView job)
    {
        var culture = CultureInfo.CurrentCulture;
        Title = job.Title;
        Peer = (job.Direction == TransferDirection.Send ? "→ " : "← ") + job.PeerName;
        Size = Format.Progress(job.TransferredBytes, job.TotalBytes);
        Fraction = job.Fraction;
        State = job.State;
        PausedByPeer = job.PausedByPeer;
        IsResumed = job.ResumedChunks > 0;
        ResumedText = IsResumed ? string.Format(culture, Strings.Transfer_Resumed, Format.Count(job.ResumedChunks)) : null;
        Meta = job.State switch
        {
            JobState.Running when job.CurrentFile is not null && job.CurrentChunkCount > 0 => string.Format(culture, Strings.Transfer_Chunk,
                job.CurrentFile, Format.Count(job.CurrentChunk), Format.Count(job.CurrentChunkCount)),
            JobState.Running => Strings.Transfer_Starting,
            JobState.Waiting => string.Format(culture, Strings.Transfer_Waiting, job.PeerName),
            JobState.AwaitingAcceptance => string.Format(culture, Strings.Transfer_AwaitingAcceptance, job.PeerName),
            JobState.Paused when job.PausedByPeer => string.Format(culture, Strings.Transfer_PausedByPeer, job.PeerName),
            JobState.Paused when job.LastError is { Length: > 0 } reason => string.Format(culture, Strings.Transfer_PausedReason, reason),
            JobState.Paused => Strings.Transfer_Paused,
            _ => "",
        };
        Rate = job.State == JobState.Running && job.BytesPerSecond > 0
            ? Format.Rate(job.BytesPerSecond) + (job.Remaining is { } left ? " · " + Format.Remaining(left) : "")
            : null;
    }
}

/// <summary>An entry under "Recently completed".</summary>
public sealed record RecentTransferViewModel(string Title, string Details, string Outcome, bool IsProblem, string When);

/// <summary>Overview: devices, running transfers, recent history (screen 01).</summary>
public sealed partial class OverviewViewModel : PageViewModel, IDisposable
{
    private readonly PairSyncCore _core;
    private readonly Func<NearbyDevice, Task> _pair;
    private readonly InternetUi? _internet;
    private readonly LatencyProbe _latency;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _timer;
    private readonly ILogger<OverviewViewModel> _logger;
    private int _refreshQueued;
    private bool _anyRunning;
    private int _ticks;
    private bool _disposed;

    private bool _anyCountdown;

    [ObservableProperty]
    private string _header = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNatBanner))]
    private NatBannerViewModel? _natBanner;

    /// <param name="pair">"Pair…" on a found device: switches to Devices and starts pairing there.</param>
    /// <param name="internet">The internet dialogs; null leaves out the internet actions.</param>
    public OverviewViewModel(PairSyncCore core, Func<NearbyDevice, Task> pair, LatencyProbe latency, TimeProvider time, InternetUi? internet = null)
        : base(AppPage.Overview)
    {
        _core = core;
        _pair = pair;
        _internet = internet;
        _core.Internet.Changed += QueueRefresh;
        _latency = latency;
        _time = time;
        _logger = core.Logger<OverviewViewModel>();
        _core.Presence.Changed += QueueRefresh;
        _core.Transfers.Changed += QueueRefresh;
        _core.Devices.Changed += QueueRefresh;
        _latency.Changed += QueueRefresh;
        // Progress while something runs; "last seen" texts and pings otherwise.
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnTick());
        _timer.Start();
        QueueRefresh();
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; } = [];

    public ObservableCollection<TransferRowViewModel> ActiveTransfers { get; } = [];

    public ObservableCollection<RecentTransferViewModel> RecentTransfers { get; } = [];

    public bool HasDevices => Devices.Count > 0;

    public bool HasActiveTransfers => ActiveTransfers.Count > 0;

    public bool HasRecentTransfers => RecentTransfers.Count > 0;

    public bool HasNatBanner => NatBanner is not null;

    public bool CanPasteCode => _internet is not null && _core.Internet.IsAvailable;

    [RelayCommand]
    private Task PasteCodeAsync() => _internet?.PasteCodeAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private Task RunDiagnosticAsync() => _internet?.ShowDiagnosticAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private Task AboutRelaysAsync() => _internet?.ShowAboutRelaysAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void DismissNatBanner()
    {
        if (NatBanner is { } banner)
            _core.Internet.Dismiss(banner.DeviceId);
    }

    private void OnTick()
    {
        if (_disposed)
            return;
        _ticks++;
        if (_anyRunning || _anyCountdown || _ticks % 30 == 0)
            QueueRefresh();
        if (_ticks % 30 == 1)
            _latency.Probe(_core.Presence.Devices.Where(d => d.State == PresenceState.Online && d.Lan is not null).Select(d => (d.Id, d.Lan!)));
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            Ui.Run(() => _ = RefreshAsync());
    }

    /// <summary>Reads presence, jobs and history again.</summary>
    public async Task RefreshAsync()
    {
        Volatile.Write(ref _refreshQueued, 0);
        if (_disposed)
            return;
        try
        {
            var jobs = await _core.Transfers.GetJobsAsync(CancellationToken.None);
            var history = await _core.Transfers.GetHistoryAsync(8, CancellationToken.None);
            if (_disposed)
                return;
            Apply(_core.Presence.Devices, jobs, history);
        }
        catch (Exception e) when (e is not OutOfMemoryException && !_disposed)
        {
            _logger.LogWarning(e, "Overview could not be refreshed");
        }
    }

    private void Apply(IReadOnlyList<NearbyDevice> devices, IReadOnlyList<JobView> jobs, IReadOnlyList<HistoryEntry> history)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var culture = CultureInfo.CurrentCulture;

        Devices.Clear();
        _anyCountdown = false;
        foreach (var device in devices)
            Devices.Add(Card(device, jobs.Count(j => j.PeerDeviceId == device.Id), now));
        NatBanner = FindNatFailure(devices);
        var paired = devices.Count(d => d.IsPaired);
        var found = devices.Count - paired;
        var pairedText = paired == 1 ? Strings.Count_PairedOne : string.Format(culture, Strings.Count_PairedMany, paired);
        Header = found == 0
            ? pairedText
            : pairedText + " · " + (found == 1 ? Strings.Count_FoundOne : string.Format(culture, Strings.Count_FoundMany, found));

        // Update rows in place, in job order.
        var rows = ActiveTransfers.ToDictionary(r => r.Id);
        for (var i = 0; i < jobs.Count; i++)
        {
            if (!rows.Remove(jobs[i].Id, out var row))
            {
                row = new TransferRowViewModel(jobs[i].Id, _core.Transfers);
                ActiveTransfers.Insert(Math.Min(i, ActiveTransfers.Count), row);
            }
            row.Update(jobs[i]);
            var index = ActiveTransfers.IndexOf(row);
            if (index != i)
                ActiveTransfers.Move(index, i);
        }
        foreach (var gone in rows.Values)
            ActiveTransfers.Remove(gone);
        _anyRunning = jobs.Any(j => j.State == JobState.Running);

        RecentTransfers.Clear();
        foreach (var entry in history)
        {
            var arrow = entry.Direction == TransferDirection.Send ? "→ " : "← ";
            RecentTransfers.Add(new RecentTransferViewModel(
                entry.Title,
                $"{arrow}{entry.PeerName} · {Format.Files(entry.FileCount)} · {Format.Bytes(entry.TotalBytes)}",
                entry.Outcome switch
                {
                    HistoryOutcome.Completed => Strings.Outcome_Completed,
                    HistoryOutcome.Canceled => Strings.Outcome_Canceled,
                    HistoryOutcome.Declined => Strings.Outcome_Declined,
                    _ => Strings.Outcome_Failed,
                },
                entry.Outcome != HistoryOutcome.Completed,
                Format.Ago(entry.FinishedAtUtc, now)));
        }

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasActiveTransfers));
        OnPropertyChanged(nameof(HasRecentTransfers));
    }

    private DeviceCardViewModel Card(NearbyDevice device, int jobs, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        if (device.State == PresenceState.Found)
            return new DeviceCardViewModel(device, CardKind.Found, null, Strings.Card_Found, null, _pair);

        var waiting = jobs switch
        {
            0 => null,
            1 => Strings.Jobs_WaitingOne,
            _ => string.Format(culture, Strings.Jobs_WaitingMany, jobs),
        };
        var lastSeen = device.LastSeenUtc is { } seen
            ? string.Format(culture, Strings.Card_LastSeen, Format.Ago(seen, now))
            : Strings.Card_NeverSeen;
        if (device.Trust == DeviceTrust.Blocked)
            return new DeviceCardViewModel(device, CardKind.Blocked, Strings.Card_Blocked, lastSeen, waiting, null);

        var internet = _core.Internet.StatusOf(device.Id);
        if (device.State == PresenceState.Online)
        {
            var route = _latency.RoundTrip(device.Id) is { } rtt
                ? string.Format(culture, Strings.Card_RouteLanRtt, Math.Max(1, (int)Math.Round(rtt.TotalMilliseconds)))
                : Strings.Card_RouteLan;
            var address = device.Lan is { Addresses.Count: > 0 } lan
                ? string.Format(culture, Strings.Card_Address, $"{lan.Addresses[0]}:{lan.Port}")
                : null;
            return new DeviceCardViewModel(device, CardKind.Connected, Strings.Card_Connected, route, address, null);
        }
        if (internet is { Phase: InternetLinkPhase.Connected })
            return new DeviceCardViewModel(device, CardKind.Connected, Strings.Card_Connected, Codes.InternetLine(internet.RoundTrip),
                Codes.RouteLine(internet.Route), null, null,
                [new CardActionViewModel(Strings.Card_Disconnect, () => _core.Internet.CloseAsync(device.Id))]);

        var (text, actions) = InternetState(device, internet, now);
        return new DeviceCardViewModel(device, CardKind.Offline, Strings.Card_Offline, lastSeen, waiting, null, text, actions);
    }

    /// <summary>What an offline card says about the internet connection and which actions it offers.</summary>
    private (string? Text, IReadOnlyList<CardActionViewModel> Actions) InternetState(NearbyDevice device, InternetLinkStatus? status, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        if (_internet is not { } ui || !_core.Internet.IsAvailable)
            return (null, []);
        CardActionViewModel Connect(bool primary = true) =>
            new(Strings.Card_ConnectInternet, () => ui.ConnectAsync(device.Id, device.Name), primary);
        CardActionViewModel Cancel() => new(Strings.Card_CancelCode, () => _core.Internet.CloseAsync(device.Id));

        switch (status?.Phase)
        {
            case InternetLinkPhase.WaitingForAnswer:
                _anyCountdown = true;
                var left = (status.ExpiresAtUtc ?? now) - now;
                return (string.Format(culture, Strings.Card_WaitingForAnswer, Codes.Countdown(left)),
                    [new(Strings.Card_PasteAnswer, () => ui.PasteCodeAsync(deviceName: device.Name), true), Cancel()]);
            case InternetLinkPhase.WaitingForConnection:
                return (string.Format(culture, Strings.Card_WaitingForApply, device.Name), [Cancel()]);
            case InternetLinkPhase.Connecting:
                return (Strings.Card_Connecting, []);
            case InternetLinkPhase.Failed:
                return (status.WasConnected ? Strings.Card_ConnectionLost : Strings.Card_ConnectionFailed, [Connect()]);
            default:
                return (null, [Connect(primary: false)]);
        }
    }

    /// <summary>The banner shows the first attempt whose ICE check failed (not a lost link, not an expired code).</summary>
    private NatBannerViewModel? FindNatFailure(IReadOnlyList<NearbyDevice> devices)
    {
        var failed = _core.Internet.Statuses
            .FirstOrDefault(s => s is { Phase: InternetLinkPhase.Failed, Failure: not null, WasConnected: false } && devices.Any(d => d.Id == s.DeviceId));
        if (failed?.Error is not { } error)
            return null;
        // "Can't connect directly to office-pc." in bold, the reason after it.
        var split = error.IndexOf(". ", StringComparison.Ordinal);
        return split < 0
            ? new NatBannerViewModel(failed.DeviceId, error, "")
            : new NatBannerViewModel(failed.DeviceId, error[..(split + 1)], error[(split + 2)..]);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _core.Presence.Changed -= QueueRefresh;
        _core.Transfers.Changed -= QueueRefresh;
        _core.Devices.Changed -= QueueRefresh;
        _core.Internet.Changed -= QueueRefresh;
        _latency.Changed -= QueueRefresh;
    }
}
