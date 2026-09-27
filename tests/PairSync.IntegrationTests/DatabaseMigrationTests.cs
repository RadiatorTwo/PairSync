using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PairSync.Storage;

namespace PairSync.IntegrationTests;

/// <summary>Upgrading databases of every earlier version (phase 6 block C).</summary>
public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-migrate-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private (DataDirectory Data, PooledDbContextFactory<PairSyncDbContext> Contexts) Open(string name)
    {
        var data = new DataDirectory(Path.Combine(_root.FullName, name));
        data.EnsureCreated();
        var options = new DbContextOptionsBuilder<PairSyncDbContext>();
        Database.Configure(options, data.DatabasePath);
        return (data, new PooledDbContextFactory<PairSyncDbContext>(options.Options));
    }

    public static TheoryData<string> EarlierMigrations()
    {
        var options = new DbContextOptionsBuilder<PairSyncDbContext>();
        Database.Configure(options, "unused.db");
        using var db = new PairSyncDbContext(options.Options);
        return [.. db.Database.GetMigrations().SkipLast(1)];
    }

    [Theory]
    [MemberData(nameof(EarlierMigrations))]
    public async Task Database_of_an_earlier_version_is_upgraded_without_losing_devices(string migration)
    {
        var (data, contexts) = Open(migration);
        var id = Guid.NewGuid();
        await using (var db = await contexts.CreateDbContextAsync(Ct))
        {
            await db.GetService<IMigrator>().MigrateAsync(migration, Ct);
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO Devices (Id, Name, PublicKey, PairedAtUtc, Trust, CanSendToMe) VALUES ({id.ToString().ToUpperInvariant()}, 'laptop', x'0102', 0, 0, 1)", Ct);
        }

        await Database.MigrateAsync(contexts, data, TimeProvider.System, Ct);

        await using (var db = await contexts.CreateDbContextAsync(Ct))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
            var device = await db.Devices.SingleAsync(Ct);
            Assert.Equal(id, device.Id);
            Assert.Equal("laptop", device.Name);
        }
        var backup = Assert.Single(Directory.GetFiles(data.BackupsDirectory, "pairsync-*.db"));
        await using var copy = new SqliteConnection($"Data Source={backup};Pooling=False");
        await copy.OpenAsync(Ct);
        await using var count = copy.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Devices";
        Assert.Equal(1L, await count.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task New_database_needs_no_backup()
    {
        var (data, contexts) = Open("fresh");

        await Database.MigrateAsync(contexts, data, TimeProvider.System, Ct);

        Assert.False(Directory.Exists(data.BackupsDirectory));
    }

    [Fact]
    public async Task Database_of_a_newer_version_is_left_untouched()
    {
        var (data, contexts) = Open("newer");
        await Database.MigrateAsync(contexts, data, TimeProvider.System, Ct);
        await using (var db = await contexts.CreateDbContextAsync(Ct))
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231000000_FromTheFuture', '99.0.0')", Ct);

        var error = await Assert.ThrowsAsync<DatabaseTooNewException>(() => Database.MigrateAsync(contexts, data, TimeProvider.System, Ct));

        Assert.Equal(["29991231000000_FromTheFuture"], error.UnknownMigrations);
        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_latest_backups_are_kept()
    {
        var (data, contexts) = Open("many");
        Directory.CreateDirectory(data.BackupsDirectory);
        for (var i = 0; i < 7; i++)
            await File.WriteAllTextAsync(Path.Combine(data.BackupsDirectory, $"pairsync-2020010{i}-000000.db"), "", Ct);
        await using (var db = await contexts.CreateDbContextAsync(Ct))
            await db.GetService<IMigrator>().MigrateAsync(EarlierMigrations().First().Data, Ct);

        await Database.MigrateAsync(contexts, data, TimeProvider.System, Ct);

        var kept = Directory.GetFiles(data.BackupsDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Database.KeptBackups, kept.Count);
        Assert.DoesNotContain("pairsync-20200100-000000.db", kept);
        Assert.Contains(kept, f => !f!.StartsWith("pairsync-2020", StringComparison.Ordinal));
    }
}
