using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.Application.Transfers;

public sealed partial class TransferService
{
    /// <summary>Manifest parts stay well below the 1 MiB control message limit.</summary>
    private const int ManifestPartBytes = 256 * 1024;

    /// <summary>
    /// One attempt to run a send job: connect, offer, wait for the receiving user, then send every open file.
    /// A lost connection puts the job back to waiting; it continues when the device is reachable again.
    /// </summary>
    private async Task RunSendJobAsync(JobRun run)
    {
        var token = run.Cancellation.Token;
        PeerConnection? connection = null;
        TransferJob? job = null;
        try
        {
            job = await LoadJobAsync(run.JobId, token, withItems: true).ConfigureAwait(false);
            if (job is null || job.State != JobState.Waiting)
                return;

            PairedDevice? device;
            await using (var db = await _contexts.CreateDbContextAsync(token).ConfigureAwait(false))
                device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == job.PeerDeviceId, token).ConfigureAwait(false);
            if (device is null)
            {
                await FinishAsync(job, JobState.Failed, "the device was removed; pair it again and send the files again").ConfigureAwait(false);
                return;
            }
            if (device.Trust == DeviceTrust.Blocked)
            {
                await SetStateAsync(job.Id, JobState.Waiting, false, $"{device.Name} is blocked").ConfigureAwait(false);
                RetryLater(job.Id, _options.RetryInterval);
                return;
            }
            if (_presence.FindEndpoint(job.PeerDeviceId) is not { } endpoint)
                return;

            connection = await _lan.ConnectAsync(device, endpoint.Addresses, endpoint.Port, token).ConfigureAwait(false);
            var control = connection.Channels.Control;
            await SendOfferAsync(control, job, token).ConfigureAwait(false);
            await SetStateAsync(job.Id, JobState.AwaitingAcceptance, false, null).ConfigureAwait(false);

            switch (await WaitForAnswerAsync(control, job.Id, token).ConfigureAwait(false))
            {
                case JobDecline decline:
                    await FinishAsync(job, JobState.Declined, decline.Reason ?? $"{device.Name} declined").ConfigureAwait(false);
                    return;
                case JobControl interrupt:
                    throw new JobInterruptedException(interrupt);
            }

            await SetStateAsync(job.Id, JobState.Running, false, null).ConfigureAwait(false);
            _logger.LogInformation("{Name} accepted job {JobId}", device.Name, job.Id);
            run.Progress.Start(job.Items.Where(i => i.State is JobItemState.Completed or JobItemState.Skipped).Sum(i => i.Size));

            foreach (var item in job.Items.Where(i => i.State is JobItemState.Pending or JobItemState.Transferring))
                await SendItemAsync(run, connection, job, item, token).ConfigureAwait(false);

            var failed = job.Items.Count(i => i.State == JobItemState.Failed);
            await control.SendAsync(new JobComplete { JobId = job.Id, FailedItems = failed }, token).ConfigureAwait(false);
            var reply = await control.ExpectAsync<JobComplete>(token).ConfigureAwait(false);
            failed = Math.Max(failed, reply.FailedItems);
            await FinishAsync(job, failed == 0 ? JobState.Completed : JobState.Failed,
                failed == 0 ? null : $"{failed} of {job.FileCount} files could not be transferred").ConfigureAwait(false);
        }
        catch (Exception) when (run.Requested is { } action && job is not null)
        {
            // Paused or canceled here: tell the other side over this connection, if there is one.
            await TrySendControlAsync(connection, job.Id, action).ConfigureAwait(false);
            if (action == JobAction.Pause)
                await SetStateAsync(job.Id, JobState.Paused, pausedByPeer: false, error: null).ConfigureAwait(false);
            else
                await FinishAsync(job, JobState.Canceled, "canceled by the user").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // shutting down, or the other device's notice stopped this run; the state is set elsewhere
        }
        catch (JobInterruptedException e) when (job is not null)
        {
            await ApplyInterruptAsync(job, e.Control).ConfigureAwait(false);
        }
        catch (TransferCanceledException e) when (e.PauseJob && job is not null)
        {
            _logger.LogInformation("Job {JobId} paused by the receiver: {Reason}", job.Id, e.Reason);
            await SetStateAsync(job.Id, JobState.Paused, pausedByPeer: true, e.Reason).ConfigureAwait(false);
        }
        catch (Exception e) when (job is not null && e is TransportException or ProtocolException or TransferCanceledException or IOException)
        {
            // Plan §12 "Peer offline: Job bleibt wartend" - no error dialog, just the reason on the card.
            var reason = e is PeerRejectedException rejected ? rejected.Message : $"waiting for the device: {e.Message}";
            _logger.LogInformation("Job {JobId} waits: {Reason}", job.Id, e.Message);
            await SetStateAsync(job.Id, JobState.Waiting, false, reason).ConfigureAwait(false);
            RetryLater(job.Id, e is PeerRejectedException ? _options.RetryInterval : TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
            _runs.TryRemove(new KeyValuePair<Guid, JobRun>(run.JobId, run));
            run.Cancellation.Dispose();
            Changed?.Invoke();
            Wake();
        }
    }

    /// <summary>Sends one file, re-planning it when it changed on disk (plan §12), up to the configured attempts.</summary>
    private async Task SendItemAsync(JobRun run, PeerConnection connection, TransferJob job, JobItem item, CancellationToken token)
    {
        if (item.IsDirectory)
        {
            await SetItemStateAsync(item, JobItemState.Completed, null).ConfigureAwait(false);
            return;
        }

        string? lastError = null;
        for (var attempt = 1; attempt <= _options.AttemptsPerFile; attempt++)
        {
            var file = new FileInfo(item.SourcePath!);
            if (!file.Exists)
            {
                lastError = "the source file no longer exists";
                break;
            }
            if (file.Length != item.Size || file.LastWriteTimeUtc != item.LastWriteTimeUtc)
                await ReplanAsync(job, item, file.Length, file.LastWriteTimeUtc, ChunkedFileSender.TransferIdFor(file)).ConfigureAwait(false);

            var sender = new ChunkedFileSender(_options.Sender with { Throttle = _throttle });
            run.Progress.FileStarted(item.RelativePath, item.Size, sender.Stats);
            try
            {
                var result = await sender.SendAsync(connection.Channels, file, token, new JobFileContext(job.Id, item.RelativePath))
                    .ConfigureAwait(false);
                if (result.Success)
                {
                    await SetItemStateAsync(item, sender.WasSkipped ? JobItemState.Skipped : JobItemState.Completed,
                        sender.WasSkipped ? "skipped: exists on the other device" : null).ConfigureAwait(false);
                    run.Progress.FileDone(item.Size);
                    return;
                }
                lastError = result.Message ?? "the receiver could not store the file";
            }
            catch (TransferCanceledException e) when (!e.PauseJob)
            {
                lastError = e.Reason; // source changed or unreadable, or the receiver refused this file
            }
            run.Progress.FileDone(0);
            _logger.LogDebug("Attempt {Attempt} for {Path} in job {JobId} failed: {Reason}", attempt, item.RelativePath, job.Id, lastError);
        }

        run.Progress.FileDone(0);
        await SetItemStateAsync(item, JobItemState.Failed, lastError).ConfigureAwait(false);
    }

    private async Task SendOfferAsync(ControlChannel control, TransferJob job, CancellationToken token)
    {
        await control.SendAsync(new JobOffer
        {
            JobId = job.Id,
            ItemCount = job.Items.Count,
            FileCount = job.Items.Count(i => !i.IsDirectory),
            TotalBytes = job.Items.Sum(i => i.Size),
            SuggestedFolder = job.TargetPath,
            Policy = job.Policy.ToWire(),
        }, token).ConfigureAwait(false);

        var part = new List<JobOfferItem>();
        var bytes = 0;
        foreach (var item in job.Items)
        {
            part.Add(new JobOfferItem
            {
                RelativePath = item.RelativePath,
                Size = item.Size,
                LastWriteTimeUtc = item.LastWriteTimeUtc,
                IsDirectory = item.IsDirectory,
            });
            bytes += Encoding.UTF8.GetByteCount(item.RelativePath) + 32;
            if (bytes < ManifestPartBytes)
                continue;
            await control.SendAsync(new JobManifest { JobId = job.Id, Items = [.. part] }, token).ConfigureAwait(false);
            part.Clear();
            bytes = 0;
        }
        if (part.Count > 0)
            await control.SendAsync(new JobManifest { JobId = job.Id, Items = [.. part] }, token).ConfigureAwait(false);
    }

    /// <summary>Waits for the receiving user; there is no timeout, the dialog may stay open for a while.</summary>
    private static async Task<IControlMessage> WaitForAnswerAsync(ControlChannel control, Guid jobId, CancellationToken token)
    {
        await foreach (var message in control.ReadAllAsync(token).ConfigureAwait(false))
        {
            switch (message)
            {
                case JobAccept accept when accept.JobId == jobId:
                    return accept;
                case JobDecline decline when decline.JobId == jobId:
                    return decline;
                case JobControl control2 when control2.JobId == jobId && control2.Action != JobAction.Resume:
                    return control2;
                case Cancel cancel:
                    throw TransferCanceledException.From(cancel, "canceled by the receiver");
            }
        }
        throw new TransportException("The connection closed before the receiver answered.");
    }

    /// <summary>The source file changed since the job was created: its new version is sent (plan §12).</summary>
    private async Task ReplanAsync(TransferJob job, JobItem item, long size, DateTime lastWriteTimeUtc, Guid transferId)
    {
        var delta = size - item.Size;
        item.Size = size;
        item.LastWriteTimeUtc = lastWriteTimeUtc;
        item.TransferId = transferId;
        await UpdateItemAsync(item.Id, stored =>
        {
            stored.Size = size;
            stored.LastWriteTimeUtc = lastWriteTimeUtc;
            stored.TransferId = transferId;
        }).ConfigureAwait(false);
        if (delta != 0)
        {
            job.TotalBytes += delta;
            await using var db = await _contexts.CreateDbContextAsync().ConfigureAwait(false);
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(set => set.SetProperty(j => j.TotalBytes, j => j.TotalBytes + delta))
                .ConfigureAwait(false);
        }
    }

    private Task SetItemStateAsync(JobItem item, JobItemState state, string? message, string? resultPath = null)
    {
        item.State = state;
        item.Message = message;
        item.ResultPath = resultPath ?? item.ResultPath;
        return UpdateItemAsync(item.Id, stored =>
        {
            stored.State = state;
            stored.Message = message;
            stored.ResultPath = item.ResultPath;
        });
    }

    /// <summary>The other device paused or canceled the job during a run.</summary>
    private async Task ApplyInterruptAsync(TransferJob job, JobControl control)
    {
        _logger.LogInformation("Job {JobId}: the other device {Action}", job.Id, control.Action);
        if (control.Action == JobAction.Cancel)
            await FinishAsync(job, JobState.Canceled, control.Reason ?? "canceled on the other device").ConfigureAwait(false);
        else
            await SetStateAsync(job.Id, JobState.Paused, pausedByPeer: true, control.Reason).ConfigureAwait(false);
    }

    private static async Task TrySendControlAsync(PeerConnection? connection, Guid jobId, JobAction action)
    {
        if (connection is null)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reason = action == JobAction.Pause ? "paused on the other device" : "canceled on the other device";
            await connection.Channels.Control.SendAsync(new JobControl { JobId = jobId, Action = action, Reason = reason }, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is TransportException or OperationCanceledException)
        {
            // the other side learns about it on the next offer
        }
    }

    private void RetryLater(Guid jobId, TimeSpan delay) => _notBefore[jobId] = _time.GetUtcNow().UtcDateTime + delay;
}
