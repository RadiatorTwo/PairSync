using Microsoft.EntityFrameworkCore;
using PairSync.SyncEngine;

namespace PairSync.Storage;

/// <summary>Chunk journal in the <c>ChunkJournal</c> table; survives app restarts.</summary>
public sealed class SqliteChunkJournal(IDbContextFactory<PairSyncDbContext> contexts, TimeProvider time) : IChunkJournal
{
    public async Task<ChunkJournalEntry?> LoadAsync(Guid transferId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ChunkJournal.AsNoTracking().SingleOrDefaultAsync(j => j.TransferId == transferId, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new ChunkJournalEntry
            {
                TransferId = row.TransferId,
                TempPath = row.TempPath,
                FileSize = row.FileSize,
                ChunkSize = row.ChunkSize,
                ChunkCount = row.ChunkCount,
                LastWriteTimeUtc = row.LastWriteTimeUtc,
                Confirmed = row.Confirmed,
            };
    }

    public async Task SaveAsync(ChunkJournalEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ChunkJournal.SingleOrDefaultAsync(j => j.TransferId == entry.TransferId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new ChunkJournalRecord { TransferId = entry.TransferId };
            db.ChunkJournal.Add(row);
        }
        row.TempPath = entry.TempPath;
        row.FileSize = entry.FileSize;
        row.ChunkSize = entry.ChunkSize;
        row.ChunkCount = entry.ChunkCount;
        row.LastWriteTimeUtc = entry.LastWriteTimeUtc;
        row.Confirmed = entry.Confirmed;
        row.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid transferId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.ChunkJournal.Where(j => j.TransferId == transferId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
