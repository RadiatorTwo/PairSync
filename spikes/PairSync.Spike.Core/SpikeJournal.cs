using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PairSync.Storage;
using PairSync.SyncEngine;

namespace PairSync.Spike;

/// <summary>The product chunk journal (SQLite) in a database next to the received files.</summary>
public static class SpikeJournal
{
    public const string FileName = "pairsync-spike.db";

    public static async Task<IChunkJournal> OpenAsync(string directory, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<PairSyncDbContext>();
        Database.Configure(options, Path.Combine(directory, FileName));
        var contexts = new PooledDbContextFactory<PairSyncDbContext>(options.Options);
        await Database.MigrateAsync(contexts, cancellationToken).ConfigureAwait(false);
        return new SqliteChunkJournal(contexts, TimeProvider.System);
    }
}
