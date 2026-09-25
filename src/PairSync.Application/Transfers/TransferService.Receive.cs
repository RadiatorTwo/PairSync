using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Application.Presence;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.Application.Transfers;

public sealed partial class TransferService
{
    /// <summary>Upper bound for items in one job, so an offer cannot exhaust memory (plan §11: size limits).</summary>
    public const int MaxItemsPerJob = 1_000_000;

    private async Task ReceiveJobAsync(PeerConnection connection, JobOffer offer)
    {
        var device = connection.Device!;
        var control = connection.Channels.Control;
        var token = _stopping.Token;

        var (items, problem) = await ReadManifestAsync(control, offer, token).ConfigureAwait(false);
        if (problem is null && !device.CanSendToMe)
            problem = "this device does not accept transfers from you";
        if (problem is not null)
        {
            _logger.LogWarning("Declined job {JobId} from {Name}: {Reason}", offer.JobId, device.Name, problem);
            await control.SendAsync(new JobDecline { JobId = offer.JobId, Reason = problem }, token).ConfigureAwait(false);
            return;
        }

        var job = await LoadJobAsync(offer.JobId, token, withItems: true).ConfigureAwait(false);
        if (job is not null)
        {
            if (job.Direction != TransferDirection.Receive || job.PeerDeviceId != device.Id)
            {
                await control.SendAsync(new JobDecline { JobId = offer.JobId, Reason = "unknown job" }, token).ConfigureAwait(false);
                return;
            }
            // The sender offers again after a disconnect or restart: answer from the stored state, without asking.
            IControlMessage? refusal = job.State switch
            {
                JobState.Paused when !job.PausedByPeer => new JobControl
                {
                    JobId = job.Id, Action = JobAction.Pause, Reason = job.LastError ?? "paused on the receiving device",
                },
                JobState.Canceled => new JobControl { JobId = job.Id, Action = JobAction.Cancel, Reason = job.LastError },
                JobState.Declined => new JobDecline { JobId = job.Id, Reason = job.LastError },
                _ => null,
            };
            if (refusal is not null)
            {
                await control.SendAsync(refusal, token).ConfigureAwait(false);
                return;
            }
            if (!job.IsFinished)
                await SetStateAsync(job.Id, JobState.Running, false, null).ConfigureAwait(false);
        }
        else
        {
            // Paired devices that may send are accepted without asking; the sender's policy applies.
            job = NewReceiveJob(offer, device, items, TargetFolderFor(device, offer), offer.Policy.FromWire());
            await using (var db = await _contexts.CreateDbContextAsync(token).ConfigureAwait(false))
            {
                db.Jobs.Add(job);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            await CreateEmptyFoldersAsync(job).ConfigureAwait(false);
            _logger.LogInformation("Accepted job {JobId} from {Name}: {Files} files ({Bytes} bytes) into {Folder}",
                job.Id, device.Name, offer.FileCount, offer.TotalBytes, job.TargetPath);
            Changed?.Invoke();
        }

        await control.SendAsync(new JobAccept { JobId = job.Id, Policy = job.Policy.ToWire() }, token).ConfigureAwait(false);
        await RunReceiveLoopAsync(connection, job).ConfigureAwait(false);
    }

    /// <summary>Reads the manifest parts and checks every path before anything is written (plan §11).</summary>
    private async Task<(List<JobOfferItem> Items, string? Problem)> ReadManifestAsync(ControlChannel control, JobOffer offer, CancellationToken token)
    {
        if (offer.ItemCount is < 0 or > MaxItemsPerJob)
            return ([], $"too many items ({offer.ItemCount})");

        var items = new List<JobOfferItem>(Math.Min(offer.ItemCount, 10_000));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (items.Count < offer.ItemCount)
        {
            var part = await control.ExpectAsync<JobManifest>(timeout.Token).ConfigureAwait(false);
            if (part.JobId != offer.JobId || part.Items is null)
                return (items, "the item list does not belong to the offer");
            items.AddRange(part.Items);
            if (items.Count > offer.ItemCount)
                return (items, "the item list is longer than announced");
        }

        foreach (var item in items)
        {
            if (RelativePaths.Check(item.RelativePath) is { } pathProblem)
                return (items, $"unsafe path {pathProblem}");
            if (item.Size < 0 || item.Size > _options.Receiver.MaxFileSize || (item.IsDirectory && item.Size != 0))
                return (items, $"'{item.RelativePath}': size {item.Size} is outside the accepted range");
        }
        if (RelativePaths.FindCaseCollision(items.Select(i => (i.RelativePath!, i.IsDirectory))) is { } clash)
            return (items, $"'{clash.First}' and '{clash.Second}' would be the same file");
        if (items.Count(i => !i.IsDirectory) != offer.FileCount || items.Sum(i => i.Size) != offer.TotalBytes)
            return (items, "file count or total size does not match the item list");
        return (items, null);
    }

    /// <summary><c>Downloads/PairSync/{sender}</c>, below it the folder the sender suggested.</summary>
    private string TargetFolderFor(PairedDevice device, JobOffer offer)
    {
        var folder = Path.Combine(_options.DownloadsFolder ?? KnownFolders.Downloads(), "PairSync", FolderNameFor(device));
        if (offer.SuggestedFolder is { } suggested && RelativePaths.IsValid(suggested))
            folder = Path.Combine([folder, .. suggested.Split('/')]);
        return folder;
    }

    private TransferJob NewReceiveJob(
        JobOffer offer, PairedDevice device, List<JobOfferItem> items, string folder, ExistingFilePolicy policy)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new TransferJob
        {
            Id = offer.JobId,
            PeerDeviceId = device.Id,
            Direction = TransferDirection.Receive,
            State = JobState.Running,
            Policy = policy,
            TargetPath = folder,
            TotalBytes = offer.TotalBytes,
            FileCount = offer.FileCount,
            Title = JobTitles.From(items.Select(i => i.RelativePath!)),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Items = [.. items.Select(i => new JobItem
            {
                RelativePath = i.RelativePath!,
                Size = i.Size,
                LastWriteTimeUtc = i.LastWriteTimeUtc,
                IsDirectory = i.IsDirectory,
            })],
        };
    }

    private async Task CreateEmptyFoldersAsync(TransferJob job)
    {
        foreach (var item in job.Items.Where(i => i.IsDirectory))
        {
            try
            {
                Directory.CreateDirectory(RelativePaths.Combine(job.TargetPath!, item.RelativePath));
                await SetItemStateAsync(item, JobItemState.Completed, null).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await SetItemStateAsync(item, JobItemState.Failed, e.Message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Receives the files the sender plans, one after another, until it reports the job complete.</summary>
    private async Task RunReceiveLoopAsync(PeerConnection connection, TransferJob job)
    {
        var run = new JobRun(job.Id, _stopping.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Task = finished.Task;
        // A new connection for the same job means the old one is dead, even if this side did not notice yet.
        if (_runs.TryRemove(job.Id, out var stale))
        {
            await stale.Cancellation.CancelAsync().ConfigureAwait(false);
            await stale.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        _runs[job.Id] = run;

        var token = run.Cancellation.Token;
        var control = connection.Channels.Control;
        var items = job.Items.ToDictionary(i => i.RelativePath, StringComparer.Ordinal);
        try
        {
            run.Progress.Start(job.Items.Where(i => i.State is JobItemState.Completed or JobItemState.Skipped).Sum(i => i.Size));
            Changed?.Invoke();
            while (true)
            {
                switch (await NextJobMessageAsync(control, job.Id, token).ConfigureAwait(false))
                {
                    case TransferPlan plan:
                        await ReceiveItemAsync(run, connection, job, items, plan, token).ConfigureAwait(false);
                        break;
                    case JobComplete:
                        var failed = items.Values.Count(i => !i.IsDirectory && i.State is not (JobItemState.Completed or JobItemState.Skipped));
                        await FinishAsync(job, failed == 0 ? JobState.Completed : JobState.Failed,
                            failed == 0 ? null : $"{failed} of {job.FileCount} files were not received").ConfigureAwait(false);
                        await control.SendAsync(new JobComplete { JobId = job.Id, FailedItems = failed }, token).ConfigureAwait(false);
                        return;
                    case JobControl interrupt:
                        throw new JobInterruptedException(interrupt);
                }
            }
        }
        catch (Exception) when (run.Requested is { } action)
        {
            await TrySendControlAsync(connection, job.Id, action).ConfigureAwait(false);
            if (action == JobAction.Pause)
                await SetStateAsync(job.Id, JobState.Paused, pausedByPeer: false, error: null).ConfigureAwait(false);
            else
                await FinishAsync(job, JobState.Canceled, "canceled by the user").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // shutting down, or replaced by a newer connection for this job
        }
        catch (JobInterruptedException e)
        {
            await ApplyInterruptAsync(job, e.Control).ConfigureAwait(false);
        }
        catch (TargetFullException e)
        {
            // Plan §12: the job pauses, temporary files stay; the user resumes after freeing space.
            _logger.LogWarning("Job {JobId} paused: {Reason}", job.Id, e.Reason);
            await SetStateAsync(job.Id, JobState.Paused, pausedByPeer: false, e.Reason).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TransportException or ProtocolException or IOException)
        {
            // The sender comes back and offers the job again; until then it waits here as running.
            _logger.LogInformation("Job {JobId} interrupted: {Reason}", job.Id, e.Message);
            await SetStateAsync(job.Id, JobState.Running, false, $"waiting for the sender: {e.Message}").ConfigureAwait(false);
        }
        finally
        {
            _runs.TryRemove(new KeyValuePair<Guid, JobRun>(job.Id, run));
            run.Cancellation.Dispose();
            finished.TrySetResult();
            Changed?.Invoke();
        }
    }

    private async Task ReceiveItemAsync(
        JobRun run, PeerConnection connection, TransferJob job, Dictionary<string, JobItem> items, TransferPlan plan, CancellationToken token)
    {
        var control = connection.Channels.Control;
        if (plan.JobId != job.Id || plan.RelativePath is null || !items.TryGetValue(plan.RelativePath, out var item) || item.IsDirectory)
        {
            await control.SendAsync(new Cancel { TransferId = plan.TransferId, Reason = "the file is not part of the job" }, token).ConfigureAwait(false);
            return;
        }

        var receiver = new ChunkedFileReceiver(job.TargetPath!, _journal, _options.Receiver);
        string target;
        try
        {
            receiver.ValidatePlan(plan with { FileName = item.RelativePath.Split('/')[^1] });
            target = RelativePaths.Combine(job.TargetPath!, item.RelativePath);
        }
        catch (Exception e) when (e is ProtocolException or ArgumentException)
        {
            await control.SendAsync(new Cancel { TransferId = plan.TransferId, Reason = e.Message }, token).ConfigureAwait(false);
            await SetItemStateAsync(item, JobItemState.Failed, e.Message).ConfigureAwait(false);
            return;
        }

        if (item.State is JobItemState.Completed or JobItemState.Skipped ||
            (job.Policy == ExistingFilePolicy.Skip && Path.Exists(target)))
        {
            if (item.State is not (JobItemState.Completed or JobItemState.Skipped))
            {
                await SetItemStateAsync(item, JobItemState.Skipped, "skipped: a file with this name exists").ConfigureAwait(false);
                run.Progress.FileDone(item.Size);
            }
            await control.SendAsync(new TransferPlanAck { TransferId = plan.TransferId, Skip = true }, token).ConfigureAwait(false);
            return;
        }

        if (plan.FileSize != item.Size || plan.LastWriteTimeUtc != item.LastWriteTimeUtc || plan.TransferId != item.TransferId)
        {
            // New version of the file on the sender (plan §12): transfer that one.
            var delta = plan.FileSize - item.Size;
            item.Size = plan.FileSize;
            item.LastWriteTimeUtc = plan.LastWriteTimeUtc;
            item.TransferId = plan.TransferId;
            await UpdateItemAsync(item.Id, stored =>
            {
                stored.Size = plan.FileSize;
                stored.LastWriteTimeUtc = plan.LastWriteTimeUtc;
                stored.TransferId = plan.TransferId;
            }).ConfigureAwait(false);
            if (delta != 0)
            {
                await using var db = await _contexts.CreateDbContextAsync(token).ConfigureAwait(false);
                await db.Jobs.Where(j => j.Id == job.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(j => j.TotalBytes, j => j.TotalBytes + delta), token).ConfigureAwait(false);
            }
        }

        run.Progress.FileStarted(item.RelativePath, item.Size, receiver.Stats);
        try
        {
            var outcome = await receiver.ReceiveAsync(connection.Channels, plan, new ReceiveTarget(target, job.Policy.ToWire()), token)
                .ConfigureAwait(false);
            if (outcome.Success)
            {
                await SetItemStateAsync(item, outcome.Skipped ? JobItemState.Skipped : JobItemState.Completed,
                    outcome.Skipped ? outcome.Message : null, outcome.FinalPath).ConfigureAwait(false);
                run.Progress.FileDone(item.Size);
            }
            else
            {
                // The sender retries; the verified temporary file (if any) is used again.
                await SetItemStateAsync(item, JobItemState.Pending, outcome.Message).ConfigureAwait(false);
                run.Progress.FileDone(0);
            }
        }
        catch (TransferCanceledException e) when (e is not TargetFullException)
        {
            // The sender gave up on this version of the file; it plans the next one or moves on.
            await SetItemStateAsync(item, JobItemState.Pending, e.Reason).ConfigureAwait(false);
            run.Progress.FileDone(0);
        }
    }

    private static async Task<IControlMessage> NextJobMessageAsync(ControlChannel control, Guid jobId, CancellationToken token)
    {
        await foreach (var message in control.ReadAllAsync(token).ConfigureAwait(false))
        {
            switch (message)
            {
                case TransferPlan or JobComplete:
                    return message;
                case JobControl interrupt when interrupt.JobId == jobId && interrupt.Action != JobAction.Resume:
                    return interrupt;
                case Ping ping:
                    await control.SendAsync(new Pong { Timestamp = ping.Timestamp }, token).ConfigureAwait(false);
                    break;
            }
        }
        throw new TransportException("The sender closed the connection.");
    }

    /// <summary>Canceled receive job: the temporary files and journal entries go (plan §12: nothing half-applied).</summary>
    private async Task DeleteTemporaryFilesAsync(Guid jobId)
    {
        var job = await LoadJobAsync(jobId, CancellationToken.None, withItems: true).ConfigureAwait(false);
        if (job?.TargetPath is null)
            return;
        foreach (var item in job.Items.Where(i => !i.IsDirectory && i.State is not (JobItemState.Completed or JobItemState.Skipped)))
        {
            try
            {
                var temp = ChunkedFileReceiver.TempPathFor(RelativePaths.Combine(job.TargetPath, item.RelativePath));
                if (File.Exists(temp))
                    File.Delete(temp);
                if (item.TransferId != Guid.Empty)
                    await _journal.DeleteAsync(item.TransferId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(e, "Could not delete the temporary file of {Path}", item.RelativePath);
            }
        }
    }

    /// <summary>The device's name as a folder name; falls back to <c>device-XXXX</c>.</summary>
    private static string FolderNameFor(PairedDevice device)
    {
        var name = string.Concat(device.Name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return RelativePaths.CheckSegment(name) is null ? name : NearbyDevice.FoundName(device.Id);
    }
}
