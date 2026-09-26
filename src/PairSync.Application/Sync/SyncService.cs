using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Connections;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.Storage.Identity;
using PairSync.Storage.Settings;
using PairSync.SyncEngine;

namespace PairSync.Application.Sync;

/// <summary>
/// Sync profiles (phase 3, plan §8): a folder kept in sync with a folder on one paired device while both apps run.
/// Each device keeps an index of its folder with a version per path, fetches the other device's index entries and
/// pulls what it lacks through the chunked transfer. Every exchange is a short session over the LAN or the internet
/// link (<see cref="PeerLinks"/>): an offer or answer, "send me your index after sequence X", "my index changed",
/// or file requests.
/// </summary>
public sealed partial class SyncService : IAsyncDisposable
{
    private readonly IDbContextFactory<PairSyncDbContext> _contexts;
    private readonly SyncIndex _index;
    private readonly PeerLinks _links;
    private readonly CurrentIdentity _identity;
    private readonly SettingsStore _settings;
    private readonly TransferService _transfers;
    private readonly IChunkJournal _journal;
    private readonly TransferOptions _transferOptions;
    private readonly SyncOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SyncService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, ProfileRun> _runs = new();

    // Held while a profile's folder or index is read or changed, so scans, rounds and applied files never interleave.
    // Kept for the life of the service: a profile that is restarted with new settings keeps its lock.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, IncomingProfileOffer> _incoming = new();
    private readonly ConcurrentDictionary<Guid, byte> _declined = new();
    private readonly ConcurrentDictionary<Guid, byte> _delivering = new();
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private int _disposed;

    public SyncService(
        IDbContextFactory<PairSyncDbContext> contexts, SyncIndex index, PeerLinks links, CurrentIdentity identity, SettingsStore settings,
        TransferService transfers, IChunkJournal journal, TransferOptions transferOptions, SyncOptions options, TimeProvider time,
        ILogger<SyncService> logger)
    {
        _contexts = contexts;
        _index = index;
        _links = links;
        _identity = identity;
        _settings = settings;
        _transfers = transfers;
        _journal = journal;
        _transferOptions = transferOptions;
        _options = options;
        _time = time;
        _logger = logger;
    }

    private Guid LocalDevice => _identity.Value.Identity.Id;

    /// <summary>A profile, its status or progress changed.</summary>
    public event Action? Changed;

    /// <summary>Another device offered a profile; see <see cref="IncomingOffers"/>.</summary>
    public event Action<IncomingProfileOffer>? OfferReceived;

    public IReadOnlyList<IncomingProfileOffer> IncomingOffers => [.. _incoming.Values];

    /// <summary>Runtime state of an active profile: one round at a time, the watcher, progress.</summary>
    private sealed class ProfileRun(Guid profileId, CancellationToken stopping) : IDisposable
    {
        public Guid ProfileId { get; } = profileId;

        /// <summary>Canceled when the profile is paused, changed or removed: a running round stops at once.</summary>
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        public SyncWatcher? Watcher { get; set; }

        public int Running;

        public int Again;

        public SyncStatus Status { get; set; } = SyncStatus.UpToDate;

        public int FilesLeft;

        public long BytesLeft;

        public long BytesDone;

        public DateTime FetchStartedUtc { get; set; }

        /// <summary>Receivers of files being fetched right now, for live progress.</summary>
        public ConcurrentDictionary<TransferStats, byte> Active { get; } = new();

        /// <summary>Problems of the last scan, so the same ones are not recorded again every round.</summary>
        public string? LastProblems { get; set; }

        public ITimer? Retry { get; set; }

        public void Dispose()
        {
            Watcher?.Dispose();
            Retry?.Dispose();
            Cancellation.Cancel();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _links.DeviceAvailable += OnDeviceAvailable;
        Background(async () =>
        {
            foreach (var profile in await LoadProfilesAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (profile.State == SyncProfileState.Active)
                    Activate(profile);
            }
            OnDeviceAvailable();
        });
        return Task.CompletedTask;
    }

    // ---- Profiles -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SyncProfileView>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var profiles = await db.SyncProfiles.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken).ConfigureAwait(false);
        var conflicts = await db.SyncConflicts.AsNoTracking().Where(c => !c.Resolved).GroupBy(c => c.ProfileId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken).ConfigureAwait(false);
        return [.. profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => View(p, names, conflicts.GetValueOrDefault(p.Id)))];
    }

    private SyncProfileView View(SyncProfile profile, IReadOnlyDictionary<Guid, string> names, int conflicts)
    {
        var run = _runs.GetValueOrDefault(profile.Id);
        var status = profile.State switch
        {
            SyncProfileState.Offered => SyncStatus.OfferPending,
            SyncProfileState.Declined => SyncStatus.Declined,
            SyncProfileState.Detached => SyncStatus.Detached,
            _ when profile.Problem is not null => SyncStatus.Problem,
            _ when profile.Paused => SyncStatus.Paused,
            _ => run?.Status ?? SyncStatus.UpToDate,
        };
        var bytesDone = run is null ? 0 : Interlocked.Read(ref run.BytesDone) + run.Active.Keys.Sum(s => s.BytesThisRun);
        var elapsed = run is null ? 0 : (_time.GetUtcNow().UtcDateTime - run.FetchStartedUtc).TotalSeconds;
        return new SyncProfileView(profile, names.GetValueOrDefault(profile.PeerDeviceId) ?? "removed device", status, conflicts,
            run is null ? 0 : Volatile.Read(ref run.FilesLeft), run is null ? 0 : Interlocked.Read(ref run.BytesLeft), bytesDone,
            status == SyncStatus.Syncing && elapsed > 0.5 ? bytesDone / elapsed : 0);
    }

    /// <summary>Creates a profile and offers it to the device; the offer waits until the device is reachable.</summary>
    /// <exception cref="SyncException">The folder or device is not usable.</exception>
    public async Task<SyncProfile> CreateProfileAsync(NewSyncProfile request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
            throw new SyncException("Enter a name for the profile.");
        var path = CheckFolder(request.LocalPath);
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == request.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new SyncException("The device is not paired.");
        if (device.Trust == DeviceTrust.Blocked)
            throw new SyncException($"{device.Name} is blocked.");
        await EnsureFolderFreeAsync(db, path, null, cancellationToken).ConfigureAwait(false);

        var profile = new SyncProfile
        {
            Id = Guid.NewGuid(),
            Name = name.Length > 128 ? name[..128] : name,
            PeerDeviceId = device.Id,
            LocalPath = path,
            Direction = request.Direction,
            Mode = request.Mode,
            Excludes = request.Excludes,
            State = SyncProfileState.Offered,
            IndexId = Guid.NewGuid(),
            CreatedAtUtc = _time.GetUtcNow().UtcDateTime,
        };
        db.SyncProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created sync profile {Name} with {Device}", profile.Name, device.Name);
        Changed?.Invoke();
        Background(() => DeliverOfferAsync(profile.Id));
        return profile;
    }

    /// <summary>Accepts an offer: stores the profile and tells the other device.</summary>
    /// <exception cref="SyncException">The offer is gone, or the folder is not usable.</exception>
    public async Task<SyncProfile> AcceptOfferAsync(Guid profileId, ProfileAcceptance acceptance, CancellationToken cancellationToken)
    {
        if (!_incoming.TryGetValue(profileId, out var offer))
            throw new SyncException("This offer is no longer open.");
        var path = CheckFolder(acceptance.LocalPath, create: true);
        SyncProfile profile;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            await EnsureFolderFreeAsync(db, path, null, cancellationToken).ConfigureAwait(false);
            profile = new SyncProfile
            {
                Id = offer.ProfileId,
                Name = offer.Name,
                PeerDeviceId = offer.DeviceId,
                LocalPath = path,
                Direction = acceptance.Direction,
                Mode = acceptance.Mode,
                Excludes = offer.Excludes,
                AllowRead = acceptance.AllowRead,
                AllowWrite = acceptance.AllowWrite,
                AllowDelete = acceptance.AllowDelete,
                State = SyncProfileState.Active,
                IndexId = Guid.NewGuid(),
                CreatedAtUtc = _time.GetUtcNow().UtcDateTime,
            };
            db.SyncProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        _incoming.TryRemove(profileId, out _);
        _logger.LogInformation("Accepted sync profile {Name} from {Device}", profile.Name, offer.DeviceName);
        Activate(profile);
        Changed?.Invoke();
        Background(() => SendNoticeAsync(profile.PeerDeviceId, new ProfileAccept { ProfileId = profile.Id, Direction = profile.Direction.ToWire() }));
        return profile;
    }

    public Task DeclineOfferAsync(Guid profileId, string? reason = null)
    {
        if (!_incoming.TryRemove(profileId, out var offer))
            return Task.CompletedTask;
        _declined[profileId] = 0;
        Changed?.Invoke();
        return SendNoticeAsync(offer.DeviceId, new ProfileDecline { ProfileId = profileId, Reason = reason ?? "declined" });
    }

    /// <summary>Ends the profile here and tells the other device; the files stay in both folders.</summary>
    public async Task RemoveProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await FindProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
            return;
        Deactivate(profileId);
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            await db.SyncProfiles.Where(p => p.Id == profileId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Removed sync profile {Name}", profile.Name);
        Changed?.Invoke();
        if (profile.State is SyncProfileState.Active or SyncProfileState.Offered)
            await SendNoticeAsync(profile.PeerDeviceId, new ProfileRemoved { ProfileId = profileId }).ConfigureAwait(false);
    }

    /// <summary>Changes direction, mode, excludes, rights or pause; the profile syncs again with the new settings.</summary>
    public async Task UpdateProfileAsync(Guid profileId, Action<SyncProfile> change, CancellationToken cancellationToken)
    {
        SyncProfile profile;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            profile = await db.SyncProfiles.SingleOrDefaultAsync(p => p.Id == profileId, cancellationToken).ConfigureAwait(false)
                ?? throw new SyncException("The profile does not exist any more.");
            change(profile);
            profile.Id = profileId;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        if (profile.State == SyncProfileState.Active)
        {
            Deactivate(profileId);
            Activate(profile);
        }
        Changed?.Invoke();
    }

    /// <summary>Pauses or resumes every active profile ("Pause all").</summary>
    public async Task SetAllPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            ids = await db.SyncProfiles.Where(p => p.State == SyncProfileState.Active && p.Paused != paused).Select(p => p.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
            await UpdateProfileAsync(id, p => p.Paused = paused, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"Sync now": scan, fetch the other index, apply, and ask the other device to do the same.</summary>
    public async Task SyncNowAsync(Guid profileId)
    {
        var profile = await FindProfileAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        if (profile is not { State: SyncProfileState.Active, Paused: false })
            return;
        await SendNoticeAsync(profile.PeerDeviceId, new SyncRequest { ProfileId = profileId }).ConfigureAwait(false);
        ScheduleRound(profileId);
    }

    public async Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SyncConflicts.AsNoTracking().Where(c => c.ProfileId == profileId && !c.Resolved).OrderBy(c => c.Path)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncActivity>> GetActivityAsync(Guid profileId, int count, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SyncActivities.AsNoTracking().Where(a => a.ProfileId == profileId).OrderByDescending(a => a.Id).Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The latest finished round of every profile, newest first (Overview "Recently completed").</summary>
    public async Task<IReadOnlyList<(SyncActivity Activity, string ProfileName)>> GetRecentRoundsAsync(int count, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.SyncActivities.AsNoTracking().Where(a => a.Kind == SyncActivityKind.Synced)
            .OrderByDescending(a => a.Id).Take(count)
            .Join(db.SyncProfiles, a => a.ProfileId, p => p.Id, (a, p) => new { a, p.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => (r.a, r.Name))];
    }

    // ---- Runtime --------------------------------------------------------------------------------------------------

    private void Activate(SyncProfile profile)
    {
        if (Volatile.Read(ref _disposed) != 0 || profile.Paused)
            return;
        var run = _runs.GetOrAdd(profile.Id, id => new ProfileRun(id, _stopping.Token));
        if (profile.Mode == SyncMode.Automatic && run.Watcher is null && Directory.Exists(profile.LocalPath))
        {
            run.Watcher = new SyncWatcher(profile.LocalPath, _options.WatcherSettle, _options.RescanInterval, _time,
                () => ScheduleRound(profile.Id),
                problem => Background(() => RecordActivityAsync(profile.Id, SyncActivityKind.Note, problem)));
        }
        ScheduleRound(profile.Id);
    }

    private SemaphoreSlim LockFor(Guid profileId) => _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));

    private void Deactivate(Guid profileId)
    {
        if (_runs.TryRemove(profileId, out var run))
            run.Dispose();
    }

    private void OnDeviceAvailable()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        Background(async () =>
        {
            foreach (var profile in await LoadProfilesAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (!_links.IsReachable(profile.PeerDeviceId))
                    continue;
                if (profile.State == SyncProfileState.Offered)
                    Background(() => DeliverOfferAsync(profile.Id));
                else if (profile is { State: SyncProfileState.Active, Paused: false, Mode: SyncMode.Automatic })
                    ScheduleRound(profile.Id);
            }
        });
    }

    /// <summary>Runs work in the background; <see cref="DisposeAsync"/> waits for it, so nothing touches the database afterwards.</summary>
    private void Background(Func<Task> work)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        var task = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException && _stopping.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _logger.LogWarning(e, "Sync background work failed");
            }
        });
        _background[task] = 0;
        task.ContinueWith(t => _background.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task<List<SyncProfile>> LoadProfilesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SyncProfiles.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SyncProfile?> FindProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SyncProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == profileId, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetStateAsync(Guid profileId, Action<SyncProfile> change)
    {
        await using (var db = await _contexts.CreateDbContextAsync(_stopping.Token).ConfigureAwait(false))
        {
            var profile = await db.SyncProfiles.SingleOrDefaultAsync(p => p.Id == profileId, _stopping.Token).ConfigureAwait(false);
            if (profile is null)
                return;
            change(profile);
            await db.SaveChangesAsync(_stopping.Token).ConfigureAwait(false);
        }
        Changed?.Invoke();
    }

    private async Task RecordActivityAsync(Guid profileId, SyncActivityKind kind, string text, int files = 0, long bytes = 0)
    {
        try
        {
            await using var db = await _contexts.CreateDbContextAsync(_stopping.Token).ConfigureAwait(false);
            db.SyncActivities.Add(new SyncActivity { ProfileId = profileId, AtUtc = _time.GetUtcNow().UtcDateTime, Kind = kind, Text = text, Files = files, Bytes = bytes });
            await db.SaveChangesAsync(_stopping.Token).ConfigureAwait(false);
            // Only the latest entries stay.
            var keepFrom = await db.SyncActivities.Where(a => a.ProfileId == profileId).OrderByDescending(a => a.Id).Skip(200)
                .Select(a => (long?)a.Id).FirstOrDefaultAsync(_stopping.Token).ConfigureAwait(false);
            if (keepFrom is { } id)
                await db.SyncActivities.Where(a => a.ProfileId == profileId && a.Id <= id).ExecuteDeleteAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DbUpdateException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(e, "Sync activity not recorded");
        }
        Changed?.Invoke();
    }

    private static string CheckFolder(string path, bool create = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new SyncException("Choose a folder.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetPathRoot(full) == full + Path.DirectorySeparatorChar || Path.GetPathRoot(full) == full)
            throw new SyncException("A whole drive cannot be a sync folder; choose a folder on it.");
        try
        {
            if (create)
                Directory.CreateDirectory(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SyncException($"The folder cannot be created: {e.Message}", e);
        }
        if (!Directory.Exists(full))
            throw new SyncException("The folder does not exist.");
        return full;
    }

    /// <summary>Two profiles must not share a folder or lie inside each other.</summary>
    private static async Task EnsureFolderFreeAsync(PairSyncDbContext db, string path, Guid? except, CancellationToken cancellationToken)
    {
        var others = await db.SyncProfiles.AsNoTracking().Where(p => p.Id != except).Select(p => new { p.Name, p.LocalPath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var comparison = SyncPaths.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var other in others)
        {
            var (a, b) = (path + Path.DirectorySeparatorChar, other.LocalPath + Path.DirectorySeparatorChar);
            if (a.StartsWith(b, comparison) || b.StartsWith(a, comparison))
                throw new SyncException($"The folder overlaps with the profile {other.Name}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _links.DeviceAvailable -= OnDeviceAvailable;
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var run in _runs.Values)
            run.Dispose();
        // Rounds, offers and notices end before the database and the transport go away.
        await Task.WhenAll(_background.Keys).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _runs.Clear();
    }
}
