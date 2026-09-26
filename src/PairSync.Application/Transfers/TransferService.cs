using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.Storage.Settings;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.Application.Transfers;

/// <summary>
/// Jobs (plan §8, §12, work package F): a sender offers files and folders to a paired device, the receiving user
/// confirms target and policy, then each file goes through the chunked transfer with its journal. Jobs survive
/// disconnects and restarts: a waiting job starts again as soon as its device is online. One connection per running
/// job; <see cref="AppSettings.ParallelTransfers"/> bounds the running send jobs.
/// </summary>
public sealed partial class TransferService : IAsyncDisposable
{
    private readonly IDbContextFactory<PairSyncDbContext> _contexts;
    private readonly SettingsStore _settings;
    private readonly PeerLinks _links;
    private readonly IChunkJournal _journal;
    private readonly TransferOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TransferService> _logger;
    private readonly UploadThrottle _throttle;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<Guid, JobRun> _runs = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _notBefore = new();
    private Task _scheduler = Task.CompletedTask;

    public TransferService(
        IDbContextFactory<PairSyncDbContext> contexts, SettingsStore settings, PeerLinks links,
        IChunkJournal journal, TransferOptions options, TimeProvider time, ILogger<TransferService> logger)
    {
        _contexts = contexts;
        _settings = settings;
        _links = links;
        _journal = journal;
        _options = options;
        _time = time;
        _logger = logger;
        _throttle = new UploadThrottle(() => settings.Current.UploadLimitBytesPerSecond, time);
    }

    /// <summary>The upload limit shared by every sender of this device (jobs and sync profiles).</summary>
    internal UploadThrottle Throttle => _throttle;

    /// <summary>A job was created or changed state. Progress is read from <see cref="GetJobsAsync"/>.</summary>
    public event Action? Changed;

    private sealed class JobRun(Guid jobId, CancellationToken stopping)
    {
        public Guid JobId { get; } = jobId;

        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        public JobProgress Progress { get; } = new();

        public bool IsSend { get; init; }

        /// <summary>What the local user asked for when the run was stopped.</summary>
        public JobAction? Requested { get; set; }

        public Task Task { get; set; } = Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _links.DeviceAvailable += OnDeviceAvailable;
        _settings.Changed += OnSettingsChanged;
        _scheduler = Task.Run(async () =>
        {
            await RecoverAfterRestartAsync(_stopping.Token).ConfigureAwait(false);
            await ScheduleLoopAsync(_stopping.Token).ConfigureAwait(false);
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Creates a send job; it starts when the device is online and a transfer slot is free.</summary>
    /// <param name="suggestedFolder">Folder name the receiver is offered below its download folder; null for none.</param>
    public async Task<Guid> SendAsync(
        Guid deviceId, SendDraft draft, ExistingFilePolicy policy, string? suggestedFolder, CancellationToken cancellationToken)
    {
        if (draft.Items.Count == 0)
            throw new ArgumentException("Nothing to send.", nameof(draft));
        if (RelativePaths.FindCaseCollision(draft.Items.Select(i => (i.RelativePath, i.IsDirectory))) is { } clash)
            throw new ArgumentException($"'{clash.First}' and '{clash.Second}' differ only in case.", nameof(draft));

        var now = _time.GetUtcNow().UtcDateTime;
        var job = new TransferJob
        {
            Id = Guid.NewGuid(),
            PeerDeviceId = deviceId,
            Direction = TransferDirection.Send,
            State = JobState.Waiting,
            Policy = policy,
            TargetPath = suggestedFolder,
            TotalBytes = draft.TotalBytes,
            FileCount = draft.FileCount,
            Title = JobTitles.From(draft.Items.Select(i => i.RelativePath)),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Items = [.. draft.Items.Select(i => new JobItem
            {
                RelativePath = i.RelativePath,
                SourcePath = i.SourcePath,
                Size = i.Size,
                LastWriteTimeUtc = i.LastWriteTimeUtc,
                IsDirectory = i.IsDirectory,
                TransferId = i.IsDirectory ? Guid.Empty : ChunkedFileSender.TransferIdFor(new FileInfo(i.SourcePath)),
            })],
        };
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await db.Devices.AnyAsync(d => d.Id == deviceId, cancellationToken).ConfigureAwait(false))
                throw new ArgumentException("The device is not paired.", nameof(deviceId));
            db.Jobs.Add(job);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        _logger.LogInformation("Created send job {JobId}: {Files} files, {Bytes} bytes", job.Id, job.FileCount, job.TotalBytes);
        Changed?.Invoke();
        Wake();
        return job.Id;
    }

    /// <summary>Unfinished jobs with live progress, oldest first.</summary>
    public async Task<IReadOnlyList<JobView>> GetJobsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.State != JobState.Completed && j.State != JobState.Canceled && j.State != JobState.Declined && j.State != JobState.Failed)
            .OrderBy(j => j.CreatedAtUtc)
            .Select(j => new
            {
                Job = j,
                Done = j.Items.Where(i => i.State == JobItemState.Completed || i.State == JobItemState.Skipped).Sum(i => i.Size),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(r =>
        {
            var view = new JobView(r.Job.Id, r.Job.Direction, r.Job.PeerDeviceId, names.GetValueOrDefault(r.Job.PeerDeviceId, "removed device"),
                r.Job.State, r.Job.PausedByPeer, r.Job.FileCount, r.Job.TotalBytes, r.Done, null, 0, 0, 0, 0, r.Job.LastError, r.Job.CreatedAtUtc)
            {
                Title = r.Job.Title,
            };
            return _runs.TryGetValue(r.Job.Id, out var run) ? run.Progress.Apply(view) : view;
        })];
    }

    /// <summary>Finished jobs, newest first ("Recently completed").</summary>
    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int count, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.History.AsNoTracking().OrderByDescending(h => h.FinishedAtUtc).Take(count).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Pauses a job on this side and tells the other device if it can be reached.</summary>
    public Task PauseAsync(Guid jobId) => ControlAsync(jobId, JobAction.Pause, "paused by the user");

    public Task ResumeAsync(Guid jobId) => ControlAsync(jobId, JobAction.Resume, null);

    /// <summary>Cancels a job on both sides; received temporary files are deleted.</summary>
    public Task CancelAsync(Guid jobId) => ControlAsync(jobId, JobAction.Cancel, "canceled by the user");

    /// <summary>Tray: "Pause all syncs".</summary>
    public async Task PauseAllAsync()
    {
        foreach (var job in await GetJobsAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (job.State != JobState.Paused || job.PausedByPeer)
                await PauseAsync(job.Id).ConfigureAwait(false);
        }
    }

    private async Task ControlAsync(Guid jobId, JobAction action, string? reason)
    {
        var job = await LoadJobAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (job is null || job.IsFinished)
            return;

        // A running job stops itself and tells the other side over its own connection.
        if (action != JobAction.Resume && _runs.TryGetValue(jobId, out var run))
        {
            run.Requested = action;
            await run.Cancellation.CancelAsync().ConfigureAwait(false);
            await run.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return;
        }

        switch (action)
        {
            case JobAction.Pause:
                await SetStateAsync(jobId, JobState.Paused, pausedByPeer: false, error: null).ConfigureAwait(false);
                break;
            case JobAction.Resume:
                if (job.State != JobState.Paused)
                    return;
                await SetStateAsync(jobId, job.Direction == TransferDirection.Send ? JobState.Waiting : JobState.Running, false, null)
                    .ConfigureAwait(false);
                break;
            case JobAction.Cancel:
                await FinishAsync(job, JobState.Canceled, reason).ConfigureAwait(false);
                break;
        }
        _notBefore.TryRemove(jobId, out _);
        Wake();
        // The sender learns about a receiver-side resume here; other changes reach the peer on the next offer too.
        await TryNotifyPeerAsync(job, action, reason).ConfigureAwait(false);
    }

    /// <summary>Tells the other device about a pause, resume or cancel while no job connection is open. Best effort.</summary>
    private async Task TryNotifyPeerAsync(TransferJob job, JobAction action, string? reason)
    {
        if (!_links.IsReachable(job.PeerDeviceId))
            return;
        try
        {
            await using var db = await _contexts.CreateDbContextAsync().ConfigureAwait(false);
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == job.PeerDeviceId).ConfigureAwait(false);
            if (device is null)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var connection = await _links.ConnectAsync(device, timeout.Token).ConfigureAwait(false);
            await connection.Channels.Control.SendAsync(new JobControl { JobId = job.Id, Action = action, Reason = reason }, timeout.Token)
                .ConfigureAwait(false);
            // Wait for the other side to close, so the message is read before the connection goes away.
            await ((Task)connection.Channels.Control.ExpectAsync<JobComplete>(timeout.Token)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (Exception e) when (e is TransportException or ProtocolException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(e, "Could not tell the other device about {Action} of job {JobId}", action, job.Id);
        }
    }

    /// <summary>Takes over an incoming session of a paired device: a job offer, or a pause/resume/cancel notice.</summary>
    /// <summary>Handles a session another device opened with a job message; owns the connection.</summary>
    public async Task HandleIncomingAsync(PeerConnection connection, IControlMessage first)
    {
        await using var owned = connection;
        try
        {
            switch (first)
            {
                case JobOffer offer:
                    await ReceiveJobAsync(connection, offer).ConfigureAwait(false);
                    break;
                case JobControl control:
                    await ApplyPeerControlAsync(connection.Device!.Id, control).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (e is TransportException or ProtocolException or OperationCanceledException or TransferCanceledException)
        {
            _logger.LogDebug(e, "Session with {Name} ended", connection.RemoteName);
        }
    }

    /// <summary>The other device paused, resumed or canceled a job while no job connection was open.</summary>
    private async Task ApplyPeerControlAsync(Guid deviceId, JobControl control)
    {
        var job = await LoadJobAsync(control.JobId, CancellationToken.None).ConfigureAwait(false);
        if (job is null || job.PeerDeviceId != deviceId || job.IsFinished)
            return;
        if (_runs.TryGetValue(job.Id, out var run))
        {
            // A notice about a job that is running over another connection: the run hears about it there, or is stale.
            if (control.Action == JobAction.Resume)
                return;
            await run.Cancellation.CancelAsync().ConfigureAwait(false);
            await run.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        switch (control.Action)
        {
            case JobAction.Pause:
                await SetStateAsync(job.Id, JobState.Paused, pausedByPeer: true, control.Reason).ConfigureAwait(false);
                break;
            case JobAction.Resume when job.State == JobState.Paused && job.PausedByPeer:
                await SetStateAsync(job.Id, job.Direction == TransferDirection.Send ? JobState.Waiting : JobState.Running, false, null)
                    .ConfigureAwait(false);
                _notBefore.TryRemove(job.Id, out _);
                Wake();
                break;
            case JobAction.Cancel:
                await FinishAsync(job, JobState.Canceled, control.Reason ?? "canceled on the other device").ConfigureAwait(false);
                break;
        }
    }

    private void OnDeviceAvailable()
    {
        foreach (var (jobId, _) in _notBefore)
            _notBefore.TryRemove(jobId, out _);
        Wake();
    }

    private void OnSettingsChanged(AppSettings settings) => Wake();

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    /// <summary>A crash or quit left jobs "running"; senders start over with an offer, receivers wait for the sender.</summary>
    private async Task RecoverAfterRestartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Jobs
            .Where(j => j.Direction == TransferDirection.Send && (j.State == JobState.Running || j.State == JobState.AwaitingAcceptance))
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.State, JobState.Waiting), cancellationToken).ConfigureAwait(false);
        await db.JobItems.Where(i => i.State == JobItemState.Transferring)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.State, JobItemState.Pending), cancellationToken).ConfigureAwait(false);
    }

    private async Task ScheduleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StartWaitingJobsAsync(cancellationToken).ConfigureAwait(false);
                await _wake.WaitAsync(_options.RetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
            {
                _logger.LogError(e, "Scheduling transfers failed");
                await Task.Delay(TimeSpan.FromSeconds(5), _time, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task StartWaitingJobsAsync(CancellationToken cancellationToken)
    {
        List<TransferJob> waiting;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            waiting = await db.Jobs.AsNoTracking()
                .Where(j => j.Direction == TransferDirection.Send && j.State == JobState.Waiting)
                .OrderBy(j => j.CreatedAtUtc)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var job in waiting)
        {
            if (_runs.Values.Count(r => r.IsSend) >= _settings.Current.ParallelTransfers)
                return;
            if (_runs.ContainsKey(job.Id) || (_notBefore.TryGetValue(job.Id, out var notBefore) && now < notBefore))
                continue;
            if (!_links.IsReachable(job.PeerDeviceId))
                continue;

            var run = new JobRun(job.Id, _stopping.Token) { IsSend = true };
            if (!_runs.TryAdd(job.Id, run))
                continue;
            run.Task = Task.Run(() => RunSendJobAsync(run), CancellationToken.None);
        }
    }

    private async Task<TransferJob?> LoadJobAsync(Guid jobId, CancellationToken cancellationToken, bool withItems = false)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Jobs.AsNoTracking();
        if (withItems)
            query = query.Include(j => j.Items.OrderBy(i => i.Id));
        return await query.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetStateAsync(Guid jobId, JobState state, bool pausedByPeer, string? error)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _contexts.CreateDbContextAsync().ConfigureAwait(false);
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(set => set
            .SetProperty(j => j.State, state)
            .SetProperty(j => j.PausedByPeer, pausedByPeer)
            .SetProperty(j => j.LastError, error)
            .SetProperty(j => j.UpdatedAtUtc, now)).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private async Task UpdateItemAsync(long itemId, Action<JobItem> change)
    {
        await using var db = await _contexts.CreateDbContextAsync().ConfigureAwait(false);
        var item = await db.JobItems.SingleAsync(i => i.Id == itemId).ConfigureAwait(false);
        change(item);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Ends a job for good and writes the history entry (plan: completed, canceled and declined jobs).</summary>
    private async Task FinishAsync(TransferJob job, JobState state, string? message)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using (var db = await _contexts.CreateDbContextAsync().ConfigureAwait(false))
        {
            var stored = await db.Jobs.SingleOrDefaultAsync(j => j.Id == job.Id).ConfigureAwait(false);
            if (stored is null || stored.IsFinished)
                return;
            stored.State = state;
            stored.LastError = state == JobState.Completed ? null : message;
            stored.UpdatedAtUtc = now;
            var peerName = await db.Devices.Where(d => d.Id == job.PeerDeviceId).Select(d => d.Name).SingleOrDefaultAsync().ConfigureAwait(false);
            db.History.Add(new HistoryEntry
            {
                JobId = job.Id,
                PeerDeviceId = job.PeerDeviceId,
                PeerName = peerName ?? "removed device",
                Direction = job.Direction,
                Title = stored.Title,
                FileCount = stored.FileCount,
                TotalBytes = stored.TotalBytes,
                StartedAtUtc = stored.CreatedAtUtc,
                FinishedAtUtc = now,
                Outcome = state switch
                {
                    JobState.Completed => HistoryOutcome.Completed,
                    JobState.Canceled => HistoryOutcome.Canceled,
                    JobState.Declined => HistoryOutcome.Declined,
                    _ => HistoryOutcome.Failed,
                },
                Message = message,
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        if (state == JobState.Canceled && job.Direction == TransferDirection.Receive)
            await DeleteTemporaryFilesAsync(job.Id).ConfigureAwait(false);
        _logger.LogInformation("Job {JobId} {State}{Message}", job.Id, state, message is null ? "" : $": {message}");
        Changed?.Invoke();
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Called by PairSyncCore while the service provider still works, and again when the provider is disposed.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _links.DeviceAvailable -= OnDeviceAvailable;
        _settings.Changed -= OnSettingsChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _scheduler.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.WhenAll(_runs.Values.Select(r => r.Task)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stopping.Dispose();
    }
}
