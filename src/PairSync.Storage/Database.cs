using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PairSync.Storage;

public static class Database
{
    public static string ConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        // The receiver journal and the UI write concurrently; wait instead of failing with SQLITE_BUSY.
        DefaultTimeout = 30,
    }.ToString();

    public static void Configure(DbContextOptionsBuilder options, string databasePath) =>
        options.UseSqlite(ConnectionString(databasePath));

    /// <summary>Applies pending migrations and switches to WAL (stored in the file, so once per start is plenty).</summary>
    public static async Task MigrateAsync(IDbContextFactory<PairSyncDbContext> contexts, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c> only.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PairSyncDbContext>
{
    public PairSyncDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PairSyncDbContext>();
        Database.Configure(options, "design-time.db");
        return new PairSyncDbContext(options.Options);
    }
}
