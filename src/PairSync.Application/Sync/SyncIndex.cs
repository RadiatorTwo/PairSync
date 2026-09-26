using Microsoft.EntityFrameworkCore;
using PairSync.Domain;
using PairSync.Storage;

namespace PairSync.Application.Sync;

/// <summary>
/// The index of each profile in SQLite (phase 3 block A): the local entries this device reports, with a sequence
/// number per change, and the remote entries the other device reported. Tombstones stay until they expire.
/// </summary>
public sealed class SyncIndex(IDbContextFactory<PairSyncDbContext> contexts)
{
    private const int Batch = 500;

    public async Task<Dictionary<string, SyncFile>> LoadAsync(Guid profileId, SyncSide side, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var files = await db.SyncFiles.AsNoTracking().Where(f => f.ProfileId == profileId && f.Side == side)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return files.ToDictionary(f => f.Path, StringComparer.Ordinal);
    }

    public async Task<SyncFile?> FindAsync(Guid profileId, SyncSide side, string path, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SyncFiles.AsNoTracking().SingleOrDefaultAsync(f => f.ProfileId == profileId && f.Side == side && f.Path == path, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Local entries changed after <paramref name="sequence"/>, oldest first.</summary>
    public async Task<IReadOnlyList<SyncFile>> LocalChangesSinceAsync(Guid profileId, long sequence, int max, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SyncFiles.AsNoTracking()
            .Where(f => f.ProfileId == profileId && f.Side == SyncSide.Local && f.Sequence > sequence)
            .OrderBy(f => f.Sequence).Take(max)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores changed local entries; each gets the next sequence number of the profile.</summary>
    /// <returns>The profile's last sequence afterwards.</returns>
    public async Task<long> RecordLocalAsync(Guid profileId, IReadOnlyCollection<SyncFile> changes, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var profile = await db.SyncProfiles.SingleAsync(p => p.Id == profileId, cancellationToken).ConfigureAwait(false);
        foreach (var batch in changes.Chunk(Batch))
        {
            foreach (var change in batch)
            {
                change.ProfileId = profileId;
                change.Side = SyncSide.Local;
                change.Sequence = ++profile.LastSequence;
            }
            await UpsertAsync(db, profileId, SyncSide.Local, batch, cancellationToken).ConfigureAwait(false);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return profile.LastSequence;
    }

    /// <summary>Stores entries the other device reported and remembers how far its index is known.</summary>
    public async Task RecordRemoteAsync(
        Guid profileId, Guid remoteIndexId, IReadOnlyCollection<SyncFile> entries, long upToSequence, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var profile = await db.SyncProfiles.SingleAsync(p => p.Id == profileId, cancellationToken).ConfigureAwait(false);
        if (profile.RemoteIndexId != remoteIndexId)
        {
            await db.SyncFiles.Where(f => f.ProfileId == profileId && f.Side == SyncSide.Remote).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            profile.RemoteIndexId = remoteIndexId;
            profile.RemoteSequenceSeen = 0;
        }
        foreach (var batch in entries.Chunk(Batch))
        {
            foreach (var entry in batch)
            {
                entry.ProfileId = profileId;
                entry.Side = SyncSide.Remote;
            }
            await UpsertAsync(db, profileId, SyncSide.Remote, batch, cancellationToken).ConfigureAwait(false);
        }
        profile.RemoteSequenceSeen = Math.Max(profile.RemoteSequenceSeen, upToSequence);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes tombstones deleted before <paramref name="before"/> on both sides.</summary>
    public async Task<int> PurgeTombstonesAsync(Guid profileId, DateTime before, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SyncFiles.Where(f => f.ProfileId == profileId && f.Deleted && f.DeletedAtUtc < before)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(PairSyncDbContext db, Guid profileId, SyncSide side, SyncFile[] batch, CancellationToken cancellationToken)
    {
        var paths = batch.Select(f => f.Path).ToList();
        var existing = await db.SyncFiles.Where(f => f.ProfileId == profileId && f.Side == side && paths.Contains(f.Path))
            .ToDictionaryAsync(f => f.Path, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var file in batch)
        {
            if (existing.TryGetValue(file.Path, out var row))
                db.Entry(row).CurrentValues.SetValues(file);
            else
                db.SyncFiles.Add(Copy(file));
        }
    }

    private static SyncFile Copy(SyncFile f) => new()
    {
        ProfileId = f.ProfileId,
        Side = f.Side,
        Path = f.Path,
        IsDirectory = f.IsDirectory,
        Size = f.Size,
        Sha256 = f.Sha256,
        ChunkHashes = f.ChunkHashes,
        MTimeUtc = f.MTimeUtc,
        Version = f.Version,
        Deleted = f.Deleted,
        DeletedAtUtc = f.DeletedAtUtc,
        Sequence = f.Sequence,
    };
}
