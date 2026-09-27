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

    public const int KeptBackups = 5;

    /// <summary>
    /// Applies pending migrations and switches to WAL (stored in the file, so once per start is plenty). Before
    /// migrating an existing database it keeps a copy in <see cref="DataDirectory.BackupsDirectory"/> (the last
    /// <see cref="KeptBackups"/>). A database from a newer PairSync is left untouched.
    /// </summary>
    /// <exception cref="DatabaseTooNewException">The database has migrations this version does not know.</exception>
    public static async Task MigrateAsync(
        IDbContextFactory<PairSyncDbContext> contexts, DataDirectory dataDirectory, TimeProvider time, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var unknown = applied.Where(m => !known.Contains(m)).ToList();
        if (unknown.Count > 0)
            throw new DatabaseTooNewException(dataDirectory.DatabasePath, unknown);

        if (applied.Count > 0 && applied.Count < known.Count)
            await BackUpAsync(db, dataDirectory, time, cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A consistent copy through SQLite's backup API, even with WAL and open connections.</summary>
    private static async Task BackUpAsync(PairSyncDbContext db, DataDirectory dataDirectory, TimeProvider time, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory.BackupsDirectory);
        var target = Path.Combine(dataDirectory.BackupsDirectory, $"pairsync-{time.GetUtcNow():yyyyMMdd-HHmmss}.db");
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
        {
            await copy.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.BackupDatabase(copy);
        }
        await connection.CloseAsync().ConfigureAwait(false);
        foreach (var old in Directory.GetFiles(dataDirectory.BackupsDirectory, "pairsync-*.db").Order(StringComparer.Ordinal).Reverse().Skip(KeptBackups))
            File.Delete(old);
    }
}

/// <summary>The database was written by a newer PairSync; this version must not touch it.</summary>
public sealed class DatabaseTooNewException(string path, IReadOnlyList<string> unknownMigrations)
    : Exception($"The database {path} was created by a newer version of PairSync. Install the newer version again, " +
                "or move the data folder aside to start fresh.")
{
    public IReadOnlyList<string> UnknownMigrations { get; } = unknownMigrations;
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
