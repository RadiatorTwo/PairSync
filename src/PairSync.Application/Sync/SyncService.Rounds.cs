using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.Application.Sync;

public sealed partial class SyncService
{
    /// <summary>Runs a round of the profile soon; a round asked for while one runs follows right after it.</summary>
    private void ScheduleRound(Guid profileId)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        var run = _runs.GetOrAdd(profileId, id => new ProfileRun(id, _stopping.Token));
        Volatile.Write(ref run.Again, 1);
        if (Interlocked.CompareExchange(ref run.Running, 1, 0) != 0)
            return;
        Background(async () =>
        {
            try
            {
                var token = run.Cancellation.Token;
                while (Interlocked.Exchange(ref run.Again, 0) == 1 && !token.IsCancellationRequested)
                {
                    try
                    {
                        await RoundAsync(profileId, run, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or DbUpdateException or InvalidOperationException
                                                  or ProtocolException or TransportException)
                    {
                        _logger.LogWarning(e, "Sync round of profile {ProfileId} failed", profileId);
                        run.Status = SyncStatus.UpToDate;
                        RetryLater(profileId, run, _options.RetryInterval);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref run.Running, 0);
            }
            if (Volatile.Read(ref run.Again) == 1 && !run.Cancellation.IsCancellationRequested)
                ScheduleRound(profileId);
            Changed?.Invoke();
        });
    }

    private void RetryLater(Guid profileId, ProfileRun run, TimeSpan delay)
    {
        run.Retry?.Dispose();
        run.Retry = _time.CreateTimer(_ => ScheduleRound(profileId), null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// One round: scan the folder, fetch the other device's new index entries, decide per path and apply. The other
    /// device is told when this device's index got changes it does not have yet.
    /// </summary>
    private async Task RoundAsync(Guid profileId, ProfileRun run, CancellationToken cancellationToken)
    {
        var profile = await FindProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is not { State: SyncProfileState.Active, Paused: false })
            return;
        PairedDevice? device;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == profile.PeerDeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            await SetStateAsync(profileId, p => p.State = SyncProfileState.Detached).ConfigureAwait(false);
            Deactivate(profileId);
            return;
        }

        var excludes = ExcludeRules.Parse(profile.Excludes);
        run.Status = SyncStatus.Scanning;
        Changed?.Invoke();
        var announce = await ScanAsync(profile, excludes, run, cancellationToken).ConfigureAwait(false);
        if (announce is null)
            return;

        if (device.Trust == DeviceTrust.Blocked || !_links.IsReachable(device.Id))
        {
            run.Status = SyncStatus.WaitingForDevice;
            return;
        }

        PeerConnection connection;
        try
        {
            connection = await _links.ConnectAsync(device, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TransportException or ProtocolException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(e, "Sync profile {Name}: {Device} not reachable", profile.Name, device.Name);
            run.Status = SyncStatus.WaitingForDevice;
            RetryLater(profileId, run, _options.RetryInterval);
            return;
        }

        await using (connection.ConfigureAwait(false))
        {
            if (connection.Handshake.RemoteMinor < 5)
            {
                await SetStateAsync(profileId, p => p.Problem = $"{device.Name} needs a newer PairSync to sync profiles.").ConfigureAwait(false);
                return;
            }
            if (!await PullIndexAsync(connection, profile, cancellationToken).ConfigureAwait(false))
            {
                await DetachAsync(profileId, $"{device.Name} removed the profile.").ConfigureAwait(false);
                return;
            }
        }
        if (profile.Problem is not null)
            await SetStateAsync(profileId, p => p.Problem = null).ConfigureAwait(false);

        profile = await FindProfileAsync(profileId, cancellationToken).ConfigureAwait(false) ?? profile;
        var result = await ApplyRemoteAsync(profile, device, excludes, run, cancellationToken).ConfigureAwait(false);
        announce = announce.Value || result.Announce;

        await ResolveVanishedConflictsAsync(profile, cancellationToken).ConfigureAwait(false);
        await SetStateAsync(profileId, p => p.LastSyncUtc = _time.GetUtcNow().UtcDateTime).ConfigureAwait(false);
        if (result.Files > 0 || result.Deleted > 0)
        {
            var text = string.Format(CultureInfo.InvariantCulture, "{0} files received, {1} deleted", result.Files, result.Deleted);
            await RecordActivityAsync(profileId, SyncActivityKind.Synced, text, result.Files, result.Bytes).ConfigureAwait(false);
        }
        run.Status = result.Incomplete ? SyncStatus.WaitingForDevice : SyncStatus.UpToDate;
        if (result.Incomplete)
            RetryLater(profileId, run, _options.RetryInterval);
        if (announce.Value)
            await SendNoticeAsync(device.Id, new SyncRequest { ProfileId = profileId }).ConfigureAwait(false);
    }

    /// <summary>Scans the folder into the index. Returns whether the index changed, or null if the folder is unusable.</summary>
    private async Task<bool?> ScanAsync(SyncProfile profile, ExcludeRules excludes, ProfileRun run, CancellationToken cancellationToken)
    {
        var gate = LockFor(profile.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var local = await _index.LoadAsync(profile.Id, SyncSide.Local, cancellationToken).ConfigureAwait(false);
            ScanResult result;
            try
            {
                result = await Task.Run(() => new FolderScanner(LocalDevice, _time).Scan(profile.LocalPath, excludes, local, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SyncFolderUnavailableException e)
            {
                await SetStateAsync(profile.Id, p => p.Problem = e.Message).ConfigureAwait(false);
                RetryLater(profile.Id, run, _options.RetryInterval);
                return null;
            }
            if (result.Changes.Count > 0)
                await _index.RecordLocalAsync(profile.Id, result.Changes, cancellationToken).ConfigureAwait(false);
            await _index.UpdateHintsAsync(profile.Id, result.Touched, cancellationToken).ConfigureAwait(false);
            if (result.Problems.Count > 0 && string.Join('\n', result.Problems) is var problems && problems != run.LastProblems)
            {
                run.LastProblems = problems;
                await RecordActivityAsync(profile.Id, SyncActivityKind.Problem,
                    $"Not synced: {string.Join("; ", result.Problems.Take(5))}{(result.Problems.Count > 5 ? $" and {result.Problems.Count - 5} more" : "")}")
                    .ConfigureAwait(false);
            }
            if (result.Busy)
                RetryLater(profile.Id, run, FolderScanner.SettleTime + TimeSpan.FromSeconds(1));

            var cutoff = _time.GetUtcNow().UtcDateTime - _options.Retention;
            await _index.PurgeTombstonesAsync(profile.Id, cutoff, cancellationToken).ConfigureAwait(false);
            PurgeOld(SyncPaths.TrashFolder(profile.LocalPath), cutoff);
            PurgeOld(SyncPaths.StagingFolder(profile.LocalPath), cutoff);
            return result.Changes.Count > 0;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Asks for the other device's index entries after the last known sequence. False if it does not know the profile.</summary>
    private async Task<bool> PullIndexAsync(PeerConnection connection, SyncProfile profile, CancellationToken cancellationToken)
    {
        var control = connection.Channels.Control;
        await control.SendAsync(new SyncHello { ProfileId = profile.Id, IndexId = profile.RemoteIndexId, SeenSequence = profile.RemoteSequenceSeen },
            cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await foreach (var message in control.ReadAllAsync(timeout.Token).ConfigureAwait(false))
        {
            switch (message)
            {
                case ProfileRemoved removed when removed.ProfileId == profile.Id:
                    return false;
                case IndexUpdate update when update.ProfileId == profile.Id:
                    var entries = (update.Entries ?? []).Select(e => SyncMapping.FromEntry(e, _transferOptions.Receiver.MaxFileSize)).OfType<SyncFile>().ToList();
                    await _index.RecordRemoteAsync(profile.Id, update.IndexId, entries, update.Last ? update.UpToSequence : 0, cancellationToken)
                        .ConfigureAwait(false);
                    if (update.Last)
                        return true;
                    break;
                case Cancel cancel:
                    throw TransferCanceledException.From(cancel, "the other device ended the index exchange");
            }
        }
        throw new TransportException("The connection closed during the index exchange.");
    }

    private sealed record ApplyResult(int Files, int Deleted, long Bytes, bool Announce, bool Incomplete);

    /// <summary>Decides per path and applies: folders, fetched files, deletions, versions, conflicts.</summary>
    private async Task<ApplyResult> ApplyRemoteAsync(SyncProfile profile, PairedDevice device, ExcludeRules excludes, ProfileRun run, CancellationToken cancellationToken)
    {
        var local = await _index.LoadAsync(profile.Id, SyncSide.Local, cancellationToken).ConfigureAwait(false);
        var remote = await _index.LoadAsync(profile.Id, SyncSide.Remote, cancellationToken).ConfigureAwait(false);
        await NoticeRemoteCopiesAsync(profile, device, local, remote, cancellationToken).ConfigureAwait(false);
        var actions = remote.Values
            .Where(r => !excludes.IsExcludedWithParents(r.Path, r.IsDirectory))
            .Select(r => SyncPlanner.Plan(profile, LocalDevice, local.GetValueOrDefault(r.Path), r))
            .Where(a => a.Kind != SyncActionKind.None)
            .ToList();
        if (actions.Count == 0)
            return new ApplyResult(0, 0, 0, false, false);

        var announce = false;
        var deleted = 0;
        var gate = LockFor(profile.Id);

        // Folders first (parents before children), then versions and notes, fetches, and deletions last (children first).
        var simple = actions.Where(a => a.Kind is SyncActionKind.CreateDirectory or SyncActionKind.AdoptVersion or SyncActionKind.KeepLocal
                                        || (a.Kind == SyncActionKind.Conflict && !ConflictNames.LocalRenames(LocalDevice, device.Id)))
            .OrderBy(a => a.Path.Count(c => c == '/')).ToList();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var action in simple)
                announce |= await ApplySimpleAsync(profile, device, action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        var fetches = actions.Where(a => a.Kind == SyncActionKind.Fetch || (a.Kind == SyncActionKind.Conflict && ConflictNames.LocalRenames(LocalDevice, device.Id)))
            .ToList();
        var fetched = fetches.Count == 0
            ? new FetchResult(0, 0, false, false)
            : await FetchAllAsync(profile, device, fetches, local, run, cancellationToken).ConfigureAwait(false);
        announce |= fetched.Announce;

        var deletions = actions.Where(a => a.Kind == SyncActionKind.Delete).OrderByDescending(a => a.Path.Count(c => c == '/'))
            .ThenByDescending(a => a.Path.Length).ToList();
        if (deletions.Count > 0)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var action in deletions)
                {
                    if (await ApplyDeleteAsync(profile, action, cancellationToken).ConfigureAwait(false))
                        deleted++;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        return new ApplyResult(fetched.Files, deleted, fetched.Bytes, announce, fetched.Incomplete);
    }

    /// <summary>Folder creation, version takeover, "kept here" and conflicts this device does not rename. Returns whether to announce.</summary>
    private async Task<bool> ApplySimpleAsync(SyncProfile profile, PairedDevice device, SyncAction action, CancellationToken cancellationToken)
    {
        var current = await _index.FindAsync(profile.Id, SyncSide.Local, action.Path, cancellationToken).ConfigureAwait(false);
        if (!SameEntry(current, action.Local))
            return false; // changed since the plan; the next round decides again
        var remote = action.Remote!;
        switch (action.Kind)
        {
            case SyncActionKind.CreateDirectory:
            {
                var full = SyncPaths.Full(profile.LocalPath, action.Path);
                if (!MatchesDisk(full, current))
                    return false;
                if (File.Exists(full))
                    MoveToTrash(profile.LocalPath, full, action.Path);
                Directory.CreateDirectory(full);
                await RecordAsync(profile.Id, new SyncFile { Path = action.Path, IsDirectory = true, MTimeUtc = Directory.GetLastWriteTimeUtc(full), Version = action.Version },
                    cancellationToken).ConfigureAwait(false);
                return action.Version != remote.Version;
            }
            case SyncActionKind.AdoptVersion:
            case SyncActionKind.KeepLocal:
            {
                var entry = CopyOf(current!);
                entry.Version = action.Version;
                await RecordAsync(profile.Id, entry, cancellationToken).ConfigureAwait(false);
                if (action.Note is { } note)
                    await RecordActivityAsync(profile.Id, SyncActivityKind.Note, note).ConfigureAwait(false);
                return action.Version != remote.Version;
            }
            case SyncActionKind.Conflict:
            {
                // The other device renames its version; this one keeps the original path and remembers the conflict.
                var copy = ConflictNames.CopyPath(action.Path, device.Id, remote.MTimeUtc);
                await AddConflictAsync(profile.Id, action.Path, copy, copyIsLocal: false, current!, remote, cancellationToken).ConfigureAwait(false);
                return false;
            }
            default:
                return false;
        }
    }

    private async Task<bool> ApplyDeleteAsync(SyncProfile profile, SyncAction action, CancellationToken cancellationToken)
    {
        var current = await _index.FindAsync(profile.Id, SyncSide.Local, action.Path, cancellationToken).ConfigureAwait(false);
        if (!SameEntry(current, action.Local))
            return false;
        var full = SyncPaths.Full(profile.LocalPath, action.Path);
        if (!MatchesDisk(full, current))
            return false;
        if (current!.IsDirectory)
        {
            if (Directory.Exists(full))
            {
                // Only an empty folder goes; files that are only here keep it.
                if (Directory.EnumerateFileSystemEntries(full).Any())
                    return false;
                Directory.Delete(full);
            }
        }
        else if (File.Exists(full))
        {
            MoveToTrash(profile.LocalPath, full, action.Path);
        }
        var now = _time.GetUtcNow().UtcDateTime;
        await RecordAsync(profile.Id, new SyncFile
        {
            Path = action.Path, IsDirectory = current.IsDirectory, Deleted = true, DeletedAtUtc = now, MTimeUtc = now, Version = action.Version,
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task DetachAsync(Guid profileId, string reason)
    {
        Deactivate(profileId);
        await SetStateAsync(profileId, p => p.State = SyncProfileState.Detached).ConfigureAwait(false);
        await RecordActivityAsync(profileId, SyncActivityKind.Note, reason).ConfigureAwait(false);
    }

    // ---- Conflicts --------------------------------------------------------------------------------------------------

    private async Task AddConflictAsync(Guid profileId, string path, string copyPath, bool copyIsLocal, SyncFile local, SyncFile remote, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.SyncConflicts.AnyAsync(c => c.ProfileId == profileId && c.CopyPath == copyPath, cancellationToken)
                .ConfigureAwait(false))
            return;
        db.SyncConflicts.Add(new SyncConflict
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            Path = path,
            CopyPath = copyPath,
            CopyIsLocal = copyIsLocal,
            LocalSize = local.Size,
            RemoteSize = remote.Size,
            LocalSha256 = local.Sha256,
            RemoteSha256 = remote.Sha256,
            LocalMTimeUtc = local.MTimeUtc,
            RemoteMTimeUtc = remote.MTimeUtc,
            DetectedAtUtc = _time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Sync conflict on {Path}; copy {Copy}", path, copyPath);
        await RecordActivityAsync(profileId, SyncActivityKind.Conflict, $"{path} was changed on both devices; both versions were kept.").ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the user's choice for a conflict. Each choice is an ordinary change of the folder (move the copy over
    /// the original, or move the copy to the trash), so it reaches the other device with the next round.
    /// </summary>
    public async Task ResolveConflictAsync(Guid conflictId, ConflictResolution resolution, CancellationToken cancellationToken)
    {
        SyncConflict? conflict;
        SyncProfile? profile;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            conflict = await db.SyncConflicts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == conflictId, cancellationToken).ConfigureAwait(false);
            profile = conflict is null ? null
                : await db.SyncProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == conflict.ProfileId, cancellationToken).ConfigureAwait(false);
        }
        if (conflict is null || profile is null || conflict.Resolved)
            return;

        var gate = LockFor(profile.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = SyncPaths.Full(profile.LocalPath, conflict.Path);
            var copy = SyncPaths.Full(profile.LocalPath, conflict.CopyPath);
            var copyWins = resolution switch
            {
                ConflictResolution.KeepThisDevice => conflict.CopyIsLocal,
                ConflictResolution.KeepOtherDevice => !conflict.CopyIsLocal,
                _ => (bool?)null,
            };
            if (copyWins == true && File.Exists(copy))
            {
                if (File.Exists(original))
                    MoveToTrash(profile.LocalPath, original, conflict.Path);
                File.Move(copy, original);
            }
            else if (copyWins == false && File.Exists(copy))
            {
                MoveToTrash(profile.LocalPath, copy, conflict.CopyPath);
            }
            await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.SyncConflicts.Where(c => c.Id == conflictId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Resolved, true), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SyncException($"The conflict could not be resolved: {e.Message}", e);
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke();
        ScheduleRound(profile.Id);
    }

    /// <summary>
    /// The other device may settle a conflict before this one sees it (it renamed its version, and this device only
    /// takes over the merged version). Its conflict copy reveals the conflict by its name.
    /// </summary>
    private async Task NoticeRemoteCopiesAsync(
        SyncProfile profile, PairedDevice device, IReadOnlyDictionary<string, SyncFile> local, IReadOnlyDictionary<string, SyncFile> remote,
        CancellationToken cancellationToken)
    {
        foreach (var copy in remote.Values.Where(r => r is { Deleted: false, IsDirectory: false }))
        {
            if (ConflictNames.OriginalOf(copy.Path, device.Id) is not { } original || local.GetValueOrDefault(original) is not { Deleted: false } mine)
                continue;
            await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await db.SyncConflicts.AnyAsync(c => c.ProfileId == profile.Id && c.CopyPath == copy.Path, cancellationToken).ConfigureAwait(false))
                    continue;
            }
            await AddConflictAsync(profile.Id, original, copy.Path, copyIsLocal: false, mine, copy, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A conflict whose copy is gone on this device (and, for the other device's copy, deleted there) is settled.</summary>
    private async Task ResolveVanishedConflictsAsync(SyncProfile profile, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var open = await db.SyncConflicts.Where(c => c.ProfileId == profile.Id && !c.Resolved).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (open.Count == 0)
            return;
        var remote = await _index.LoadAsync(profile.Id, SyncSide.Remote, cancellationToken).ConfigureAwait(false);
        var changed = false;
        foreach (var conflict in open)
        {
            var copyHere = File.Exists(SyncPaths.Full(profile.LocalPath, conflict.CopyPath));
            var copyThere = remote.GetValueOrDefault(conflict.CopyPath);
            if (!copyHere && (conflict.CopyIsLocal || copyThere is { Deleted: true }))
            {
                conflict.Resolved = true;
                changed = true;
            }
        }
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
        }
    }

    // ---- Folder helpers ---------------------------------------------------------------------------------------------

    private Task RecordAsync(Guid profileId, SyncFile entry, CancellationToken cancellationToken) =>
        _index.RecordLocalAsync(profileId, [entry], cancellationToken);

    /// <summary>The index entry is still the one the plan was made with.</summary>
    private static bool SameEntry(SyncFile? current, SyncFile? planned) =>
        (current is null && planned is null) ||
        (current is not null && planned is not null && current.Version == planned.Version && current.SameContentAs(planned));

    /// <summary>The folder still holds what the index entry says (nothing if there is none or it is a tombstone).</summary>
    private static bool MatchesDisk(string full, SyncFile? entry)
    {
        if (entry is null || entry.Deleted)
            return !File.Exists(full) && !Directory.Exists(full);
        if (entry.IsDirectory)
            return Directory.Exists(full);
        var file = new FileInfo(full);
        return file.Exists && file.Length == entry.Size && file.LastWriteTimeUtc == entry.MTimeUtc;
    }

    /// <summary>Moves a replaced or deleted file to <c>.pairsync/trash/{date}/{path}</c>, kept for the retention time.</summary>
    private void MoveToTrash(string root, string full, string path)
    {
        var day = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var target = SyncPaths.Full(Path.Combine(SyncPaths.TrashFolder(root), day), path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var unique = target;
        for (var i = 2; File.Exists(unique) || Directory.Exists(unique); i++)
            unique = $"{target} ({i})";
        File.Move(full, unique);
    }

    private static void PurgeOld(string folder, DateTime cutoff)
    {
        if (!Directory.Exists(folder))
            return;
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                var date = DateTime.TryParseExact(entry.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                    ? day
                    : entry.LastWriteTimeUtc;
                if (date >= cutoff)
                    continue;
                if (entry is DirectoryInfo directory)
                    directory.Delete(recursive: true);
                else
                    entry.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Tried again with the next round.
        }
    }

    private static SyncFile CopyOf(SyncFile f) => new()
    {
        Path = f.Path,
        IsDirectory = f.IsDirectory,
        Size = f.Size,
        Sha256 = f.Sha256,
        ChunkHashes = f.ChunkHashes,
        MTimeUtc = f.MTimeUtc,
        Version = f.Version,
        Deleted = f.Deleted,
        DeletedAtUtc = f.DeletedAtUtc,
    };
}
