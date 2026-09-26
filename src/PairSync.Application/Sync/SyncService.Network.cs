using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    /// <summary>Whether a session that starts with <paramref name="first"/> belongs to sync profiles.</summary>
    public static bool IsSyncMessage(IControlMessage first) =>
        first is ProfileOffer or ProfileAccept or ProfileDecline or ProfileRemoved or SyncHello or SyncRequest or FileRequest;

    /// <summary>Handles a session another device opened with a sync message; owns the connection.</summary>
    public async Task HandleIncomingAsync(PeerConnection connection, IControlMessage first)
    {
        await using var owned = connection;
        var device = connection.Device!;
        try
        {
            switch (first)
            {
                case ProfileOffer offer:
                    await OnOfferAsync(connection, device, offer).ConfigureAwait(false);
                    break;
                case ProfileAccept accept:
                    await OnAcceptAsync(device, accept.ProfileId, accept.Direction.FromWire()).ConfigureAwait(false);
                    break;
                case ProfileDecline decline:
                    if (await OwnedProfileAsync(decline.ProfileId, device.Id) is { State: SyncProfileState.Offered })
                    {
                        await SetStateAsync(decline.ProfileId, p => p.State = SyncProfileState.Declined).ConfigureAwait(false);
                        await RecordActivityAsync(decline.ProfileId, SyncActivityKind.Note, $"{device.Name} declined the profile.").ConfigureAwait(false);
                    }
                    break;
                case ProfileRemoved removed:
                    _incoming.TryRemove(removed.ProfileId, out _);
                    if (await OwnedProfileAsync(removed.ProfileId, device.Id) is { State: SyncProfileState.Active or SyncProfileState.Offered })
                        await DetachAsync(removed.ProfileId, $"{device.Name} removed the profile.").ConfigureAwait(false);
                    Changed?.Invoke();
                    break;
                case SyncHello hello:
                    await ServeIndexAsync(connection, device, hello).ConfigureAwait(false);
                    break;
                case SyncRequest request:
                    if (await OwnedProfileAsync(request.ProfileId, device.Id) is { } profile)
                    {
                        if (profile.State == SyncProfileState.Offered)
                            await OnAcceptAsync(device, profile.Id, null).ConfigureAwait(false);
                        else if (profile is { State: SyncProfileState.Active, Paused: false })
                            ScheduleRound(profile.Id);
                    }
                    else
                    {
                        await connection.Channels.Control.SendAsync(new ProfileRemoved { ProfileId = request.ProfileId }, _stopping.Token).ConfigureAwait(false);
                    }
                    break;
                case FileRequest request:
                    await ServeFilesAsync(connection, device, request).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (e is TransportException or ProtocolException or OperationCanceledException or TransferCanceledException
                                      or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(e, "Sync session with {Name} ended", device.Name);
        }
    }

    private async Task<SyncProfile?> OwnedProfileAsync(Guid profileId, Guid deviceId) =>
        await FindProfileAsync(profileId, _stopping.Token).ConfigureAwait(false) is { } profile && profile.PeerDeviceId == deviceId ? profile : null;

    private async Task OnOfferAsync(PeerConnection connection, PairedDevice device, ProfileOffer offer)
    {
        var control = connection.Channels.Control;
        var existing = await FindProfileAsync(offer.ProfileId, _stopping.Token).ConfigureAwait(false);
        if (existing is not null)
        {
            // The other device missed the answer: repeat it.
            if (existing.PeerDeviceId == device.Id && existing.State == SyncProfileState.Active)
                await control.SendAsync(new ProfileAccept { ProfileId = existing.Id, Direction = existing.Direction.ToWire() }, _stopping.Token).ConfigureAwait(false);
            return;
        }
        if (_declined.ContainsKey(offer.ProfileId))
        {
            await control.SendAsync(new ProfileDecline { ProfileId = offer.ProfileId, Reason = "declined" }, _stopping.Token).ConfigureAwait(false);
            return;
        }
        var name = string.Concat((offer.Name ?? "").Where(c => !char.IsControl(c))).Trim();
        var incoming = new IncomingProfileOffer(offer.ProfileId, device.Id, device.Name, name.Length == 0 ? "Sync" : name[..Math.Min(name.Length, 128)],
            offer.Direction.FromWire(), offer.Excludes ?? "");
        if (_incoming.TryAdd(offer.ProfileId, incoming))
        {
            _logger.LogInformation("{Device} offers the sync profile {Name}", device.Name, incoming.Name);
            OfferReceived?.Invoke(incoming);
            Changed?.Invoke();
        }
    }

    /// <summary>The other device accepted (or started syncing, which implies it): the offered profile becomes active.</summary>
    private async Task OnAcceptAsync(PairedDevice device, Guid profileId, SyncDirection? theirDirection)
    {
        if (await OwnedProfileAsync(profileId, device.Id) is not { State: SyncProfileState.Offered })
            return;
        await SetStateAsync(profileId, p => p.State = SyncProfileState.Active).ConfigureAwait(false);
        _logger.LogInformation("{Device} accepted sync profile {ProfileId} ({Direction})", device.Name, profileId, theirDirection);
        if (await FindProfileAsync(profileId, _stopping.Token).ConfigureAwait(false) is { } profile)
            Activate(profile);
    }

    /// <summary>Sends the offer of a profile in state Offered; an answer on the same session is applied right away.</summary>
    private async Task DeliverOfferAsync(Guid profileId)
    {
        if (!_delivering.TryAdd(profileId, 0))
            return;
        try
        {
            var profile = await FindProfileAsync(profileId, _stopping.Token).ConfigureAwait(false);
            if (profile is not { State: SyncProfileState.Offered })
                return;
            var device = await DeviceAsync(profile.PeerDeviceId).ConfigureAwait(false);
            if (device is null || !_links.IsReachable(device.Id))
                return;
            await using var connection = await _links.ConnectAsync(device, _stopping.Token).ConfigureAwait(false);
            if (connection.Handshake.RemoteMinor < 5)
            {
                await SetStateAsync(profileId, p => p.Problem = $"{device.Name} needs a newer PairSync to sync profiles.").ConfigureAwait(false);
                return;
            }
            var control = connection.Channels.Control;
            await control.SendAsync(new ProfileOffer
            {
                ProfileId = profile.Id, Name = profile.Name, Direction = profile.Direction.ToWire(), Excludes = profile.Excludes,
            }, _stopping.Token).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await foreach (var message in control.ReadAllAsync(wait.Token).ConfigureAwait(false))
                {
                    if (message is ProfileAccept accept && accept.ProfileId == profileId)
                    {
                        await OnAcceptAsync(device, profileId, accept.Direction.FromWire()).ConfigureAwait(false);
                        break;
                    }
                    if (message is ProfileDecline decline && decline.ProfileId == profileId)
                    {
                        await SetStateAsync(profileId, p => p.State = SyncProfileState.Declined).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
            {
                // No immediate answer: the user decides later and the answer comes in its own session.
            }
        }
        catch (Exception e) when (e is TransportException or ProtocolException or OperationCanceledException)
        {
            _logger.LogDebug(e, "Offer of sync profile {ProfileId} not delivered; tried again when the device is reachable", profileId);
        }
        finally
        {
            _delivering.TryRemove(profileId, out _);
        }
    }

    /// <summary>Sends a one-message notice (answer, removal, "index changed"); best effort, the other device catches up later.</summary>
    private async Task SendNoticeAsync(Guid deviceId, IControlMessage notice)
    {
        try
        {
            var device = await DeviceAsync(deviceId).ConfigureAwait(false);
            if (device is null || device.Trust == DeviceTrust.Blocked || !_links.IsReachable(deviceId))
                return;
            await using var connection = await _links.ConnectAsync(device, _stopping.Token).ConfigureAwait(false);
            if (connection.Handshake.RemoteMinor < 5)
                return;
            await connection.Channels.Control.SendAsync(notice, _stopping.Token).ConfigureAwait(false);
            // Give the other side a moment to read it before the session closes.
            using var linger = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            linger.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await foreach (var message in connection.Channels.Control.ReadAllAsync(linger.Token).ConfigureAwait(false))
                {
                    if (message is ProfileRemoved removed)
                        await DetachAsync(removed.ProfileId, $"{device.Name} does not know the profile any more.").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
            {
            }
        }
        catch (Exception e) when (e is TransportException or ProtocolException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(e, "{Notice} to {DeviceId} not delivered", notice.GetType().Name, deviceId);
        }
    }

    private async Task<PairedDevice?> DeviceAsync(Guid deviceId)
    {
        await using var db = await _contexts.CreateDbContextAsync(_stopping.Token).ConfigureAwait(false);
        return await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId, _stopping.Token).ConfigureAwait(false);
    }

    // ---- Serving ----------------------------------------------------------------------------------------------------

    /// <summary>Answers <see cref="SyncHello"/> with the local index entries after the sequence the other device knows.</summary>
    private async Task ServeIndexAsync(PeerConnection connection, PairedDevice device, SyncHello hello)
    {
        var control = connection.Channels.Control;
        var profile = await OwnedProfileAsync(hello.ProfileId, device.Id).ConfigureAwait(false);
        if (profile is null || profile.State is SyncProfileState.Declined or SyncProfileState.Detached)
        {
            await control.SendAsync(new ProfileRemoved { ProfileId = hello.ProfileId }, _stopping.Token).ConfigureAwait(false);
            return;
        }
        if (profile.State == SyncProfileState.Offered)
        {
            await OnAcceptAsync(device, profile.Id, null).ConfigureAwait(false);
            profile = await FindProfileAsync(profile.Id, _stopping.Token).ConfigureAwait(false) ?? profile;
        }

        var since = hello.IndexId == profile.IndexId ? hello.SeenSequence : 0;
        var upTo = profile.LastSequence;
        var batch = new List<IndexEntry>();
        var size = 0;
        while (true)
        {
            var changes = await _index.LocalChangesSinceAsync(profile.Id, since, 1000, _stopping.Token).ConfigureAwait(false);
            foreach (var change in changes.Where(c => c.Sequence <= upTo))
            {
                var entry = change.ToEntry(_options.MaxChunkHashBytes);
                var entrySize = entry.EstimatedSize();
                if (batch.Count > 0 && size + entrySize > _options.IndexUpdateBytes)
                {
                    await control.SendAsync(new IndexUpdate { ProfileId = profile.Id, IndexId = profile.IndexId, Entries = [.. batch] }, _stopping.Token)
                        .ConfigureAwait(false);
                    batch.Clear();
                    size = 0;
                }
                batch.Add(entry);
                size += entrySize;
            }
            if (changes.Count < 1000 || changes[^1].Sequence >= upTo)
                break;
            since = changes[^1].Sequence;
        }
        await control.SendAsync(new IndexUpdate { ProfileId = profile.Id, IndexId = profile.IndexId, Entries = [.. batch], UpToSequence = upTo, Last = true },
            _stopping.Token).ConfigureAwait(false);
        // Wait until the other side closes, so the last message is not cut off.
        using var linger = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        linger.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var _ in control.ReadAllAsync(linger.Token).ConfigureAwait(false))
            {
            }
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>Answers file requests one after the other until the other side closes the session.</summary>
    private async Task ServeFilesAsync(PeerConnection connection, PairedDevice device, FileRequest first)
    {
        var control = connection.Channels.Control;
        var request = first;
        while (true)
        {
            if (await ServeFileAsync(connection, device, request).ConfigureAwait(false) is { } reason)
            {
                await control.SendAsync(new FileUnavailable { ProfileId = request.ProfileId, Path = request.Path, Reason = reason }, _stopping.Token)
                    .ConfigureAwait(false);
            }
            FileRequest? next = null;
            await foreach (var message in control.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (message is FileRequest more)
                {
                    next = more;
                    break;
                }
            }
            if (next is null)
                return;
            request = next;
        }
    }

    /// <summary>Sends one file if allowed and unchanged; returns why not otherwise.</summary>
    private async Task<string?> ServeFileAsync(PeerConnection connection, PairedDevice device, FileRequest request)
    {
        var profile = await OwnedProfileAsync(request.ProfileId, device.Id).ConfigureAwait(false);
        if (profile is not { State: SyncProfileState.Active } || profile.Paused)
            return "the profile is not active on this device";
        if (!profile.DeliversFiles)
            return "this device does not give out files of this profile";
        if (request.Path is not { } path || SyncPaths.Check(path) is not null)
            return "invalid path";
        var entry = await _index.FindAsync(profile.Id, SyncSide.Local, path, _stopping.Token).ConfigureAwait(false);
        if (entry is null || entry.Deleted || entry.IsDirectory || request.Sha256 is null || entry.Sha256 is null
            || !entry.Sha256.AsSpan().SequenceEqual(request.Sha256))
            return "this version is not here any more";
        var file = new FileInfo(SyncPaths.Full(profile.LocalPath, path));
        if (!file.Exists || file.Length != entry.Size || file.LastWriteTimeUtc != entry.MTimeUtc)
            return "the file changed since it was indexed";
        var sender = new ChunkedFileSender(_transferOptions.Sender with { Throttle = _transfers.Throttle });
        await sender.SendAsync(connection.Channels, file, _stopping.Token).ConfigureAwait(false);
        return null;
    }

    // ---- Fetching ---------------------------------------------------------------------------------------------------

    private sealed record FetchResult(int Files, long Bytes, bool Announce, bool Incomplete);

    /// <summary>Fetches files over up to <c>ParallelTransfers</c> sessions and moves each into place.</summary>
    private async Task<FetchResult> FetchAllAsync(
        SyncProfile profile, PairedDevice device, List<SyncAction> fetches, IReadOnlyDictionary<string, SyncFile> local, ProfileRun run,
        CancellationToken cancellationToken)
    {
        var queue = new ConcurrentQueue<SyncAction>(fetches.OrderBy(a => a.Remote!.Size));
        run.Status = SyncStatus.Syncing;
        Volatile.Write(ref run.FilesLeft, fetches.Count);
        Interlocked.Exchange(ref run.BytesLeft, fetches.Sum(a => a.Remote!.Size));
        Interlocked.Exchange(ref run.BytesDone, 0);
        run.FetchStartedUtc = _time.GetUtcNow().UtcDateTime;
        Changed?.Invoke();

        var byHash = local.Values.Where(f => f is { Deleted: false, IsDirectory: false, Sha256: not null, ChunkHashes: not null })
            .GroupBy(f => Convert.ToHexString(f.Sha256!)).ToDictionary(g => g.Key, g => g.First());
        var files = 0;
        long bytes = 0;
        var announce = false;
        var incomplete = false;
        var workers = Math.Clamp(_settings.Current.ParallelTransfers, 1, 8);
        await Task.WhenAll(Enumerable.Range(0, Math.Min(workers, fetches.Count)).Select(_ => Task.Run(async () =>
        {
            PeerConnection? connection = null;
            try
            {
                while (queue.TryDequeue(out var action))
                {
                    connection ??= await _links.ConnectAsync(device, cancellationToken).ConfigureAwait(false);
                    var outcome = await FetchOneAsync(connection, profile, action, local, byHash, run, cancellationToken).ConfigureAwait(false);
                    Interlocked.Decrement(ref run.FilesLeft);
                    Interlocked.Add(ref run.BytesLeft, -action.Remote!.Size);
                    if (outcome is { } applied)
                    {
                        Interlocked.Increment(ref files);
                        Interlocked.Add(ref bytes, action.Remote.Size);
                        if (applied)
                            announce = true;
                    }
                    Changed?.Invoke();
                }
            }
            catch (Exception e) when (e is TransportException or ProtocolException or TargetFullException
                                          or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(e, "Sync profile {Name}: fetching from {Device} stopped", profile.Name, device.Name);
                incomplete = true;
                if (e is TargetFullException full)
                    await RecordActivityAsync(profile.Id, SyncActivityKind.Problem, full.Message).ConfigureAwait(false);
            }
            finally
            {
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
            }
        }, cancellationToken))).ConfigureAwait(false);
        Volatile.Write(ref run.FilesLeft, 0);
        Interlocked.Exchange(ref run.BytesLeft, 0);
        return new FetchResult(files, bytes, announce, incomplete || !queue.IsEmpty);
    }

    /// <summary>
    /// Fetches one file into the staging folder and moves it into place. Returns null if nothing was applied,
    /// otherwise whether this device's index got a version the other device lacks (conflict copy).
    /// </summary>
    private async Task<bool?> FetchOneAsync(
        PeerConnection connection, SyncProfile profile, SyncAction action, IReadOnlyDictionary<string, SyncFile> local,
        IReadOnlyDictionary<string, SyncFile> byHash, ProfileRun run, CancellationToken cancellationToken)
    {
        var remote = action.Remote!;
        var control = connection.Channels.Control;
        await control.SendAsync(new FileRequest { ProfileId = profile.Id, Path = remote.Path, Sha256 = remote.Sha256 }, cancellationToken).ConfigureAwait(false);
        TransferPlan? plan = null;
        await foreach (var message in control.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (message is FileUnavailable unavailable && unavailable.Path == remote.Path)
            {
                _logger.LogDebug("{Path} not fetched: {Reason}", remote.Path, unavailable.Reason);
                return null;
            }
            if (message is TransferPlan received)
            {
                plan = received;
                break;
            }
            if (message is Cancel cancel)
                throw TransferCanceledException.From(cancel, "the other device canceled");
        }
        if (plan is null)
            throw new TransportException("The connection closed before the file arrived.");

        var staging = Path.Combine(SyncPaths.StagingFolder(profile.LocalPath), StagingName(remote));
        var receiver = new ChunkedFileReceiver(Path.GetDirectoryName(staging)!, _journal, _transferOptions.Receiver);
        try
        {
            receiver.ValidatePlan(plan);
            if (plan.FileSize != remote.Size)
                throw new ProtocolException($"{remote.Path}: the other device sent {plan.FileSize} bytes, its index says {remote.Size}.");
        }
        catch (ProtocolException e)
        {
            await control.SendAsync(new Cancel { TransferId = plan.TransferId, Reason = e.Message }, cancellationToken).ConfigureAwait(false);
            return null;
        }

        run.Active[receiver.Stats] = 0;
        ReceiveOutcome outcome;
        try
        {
            outcome = await receiver.ReceiveAsync(connection.Channels, plan,
                new ReceiveTarget(staging, ExistingFileAction.Replace, SeedFor(profile, remote, local, byHash)), cancellationToken).ConfigureAwait(false);
        }
        catch (TransferCanceledException e)
        {
            _logger.LogDebug(e, "{Path} not fetched", remote.Path);
            return null;
        }
        finally
        {
            run.Active.TryRemove(receiver.Stats, out _);
            Interlocked.Add(ref run.BytesDone, receiver.Stats.BytesThisRun);
        }
        if (!outcome.Success)
        {
            await RecordActivityAsync(profile.Id, SyncActivityKind.Problem, $"{remote.Path}: {outcome.Message}").ConfigureAwait(false);
            return null;
        }
        return await PlaceAsync(profile, action, staging, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The local file with matching chunks: the old version at the same path, or any file with the same content (moved or copied).</summary>
    private static ChunkSeed? SeedFor(SyncProfile profile, SyncFile remote, IReadOnlyDictionary<string, SyncFile> local, IReadOnlyDictionary<string, SyncFile> byHash)
    {
        if (remote.ChunkHashes is null)
            return null;
        var source = byHash.GetValueOrDefault(Convert.ToHexString(remote.Sha256!))
                     ?? (local.GetValueOrDefault(remote.Path) is { Deleted: false, IsDirectory: false, ChunkHashes: not null } old ? old : null);
        return source is null ? null : new ChunkSeed(SyncPaths.Full(profile.LocalPath, source.Path), source.ChunkHashes!, remote.ChunkHashes);
    }

    /// <summary>Same name for the same wanted version, so an interrupted fetch resumes from its temporary file.</summary>
    private static string StagingName(SyncFile remote) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(remote.Path + "|" + Convert.ToHexString(remote.Sha256!))).AsSpan(0, 16));

    /// <summary>Moves a fetched file into place if the folder still is as planned. Returns null if it was not placed.</summary>
    private async Task<bool?> PlaceAsync(SyncProfile profile, SyncAction action, string staging, CancellationToken cancellationToken)
    {
        var remote = action.Remote!;
        var gate = LockFor(profile.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _index.FindAsync(profile.Id, SyncSide.Local, action.Path, cancellationToken).ConfigureAwait(false);
            var full = SyncPaths.Full(profile.LocalPath, action.Path);
            if (!SameEntry(current, action.Local) || !MatchesDisk(full, current))
                return null; // changed meanwhile: the next scan records it and the next round decides again

            var announce = false;
            if (action.Kind == SyncActionKind.Conflict)
            {
                // This device has the smaller id: its version moves aside, the other one takes the original path.
                var copyPath = ConflictNames.CopyPath(action.Path, LocalDevice, current!.MTimeUtc);
                var copyFull = SyncPaths.Full(profile.LocalPath, copyPath);
                if (File.Exists(copyFull) || Directory.Exists(copyFull))
                {
                    await RecordActivityAsync(profile.Id, SyncActivityKind.Problem, $"{action.Path}: the conflict copy {copyPath} exists already.").ConfigureAwait(false);
                    return null;
                }
                File.Move(full, copyFull);
                var previousCopy = await _index.FindAsync(profile.Id, SyncSide.Local, copyPath, cancellationToken).ConfigureAwait(false);
                var copy = CopyOf(current);
                copy.Path = copyPath;
                copy.Version = (previousCopy?.VersionVector ?? VersionVector.Empty).Increment(LocalDevice).ToString();
                await RecordAsync(profile.Id, copy, cancellationToken).ConfigureAwait(false);
                await AddConflictAsync(profile.Id, action.Path, copyPath, copyIsLocal: true, current, remote, cancellationToken).ConfigureAwait(false);
                announce = true;
            }
            else if (current is { Deleted: false, IsDirectory: true })
            {
                if (Directory.EnumerateFileSystemEntries(full).Any())
                {
                    await RecordActivityAsync(profile.Id, SyncActivityKind.Problem, $"{action.Path}: a folder with files is in the way.").ConfigureAwait(false);
                    return null;
                }
                Directory.Delete(full);
            }
            else if (File.Exists(full))
            {
                MoveToTrash(profile.LocalPath, full, action.Path);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Move(staging, full);
            File.SetLastWriteTimeUtc(full, remote.MTimeUtc);
            await RecordAsync(profile.Id, new SyncFile
            {
                Path = action.Path,
                Size = remote.Size,
                Sha256 = remote.Sha256,
                ChunkHashes = remote.ChunkHashes ?? FileHashes.Compute(full, cancellationToken).ChunkHashes,
                MTimeUtc = File.GetLastWriteTimeUtc(full),
                Version = action.Version,
            }, cancellationToken).ConfigureAwait(false);
            if (action.Note is { } note)
                await RecordActivityAsync(profile.Id, SyncActivityKind.Note, note).ConfigureAwait(false);
            return announce || action.Version != remote.Version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await RecordActivityAsync(profile.Id, SyncActivityKind.Problem, $"{action.Path}: {e.Message}").ConfigureAwait(false);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }
}
