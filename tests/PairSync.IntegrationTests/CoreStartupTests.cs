using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Domain;
using PairSync.Storage;
using PairSync.Storage.Secrets;
using PairSync.Storage.Settings;
using PairSync.SyncEngine;

namespace PairSync.IntegrationTests;

public sealed class CoreStartupTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-core-");

    public void Dispose()
    {
        // Pooled SQLite connections keep the file open until the pool is cleared.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private DataDirectory Data => new(Path.Combine(_root.FullName, "data"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_start_creates_database_logs_and_default_settings()
    {
        await using (var core = await PairSyncCore.StartAsync(Data, Ct))
        {
            var contexts = core.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>();
            await using var db = await contexts.CreateDbContextAsync(Ct);

            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Ct), m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
            var mode = await db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync(Ct);
            Assert.Equal("wal", Assert.Single(mode));
            Assert.Equal(AppSettings.DefaultPort, core.Settings.Current.Port);
            if (OperatingSystem.IsWindows())
                Assert.Equal(SecretProtection.Dpapi, core.Identity.Protection);
            Assert.True(File.Exists(Path.Combine(Data.Root, "identity.json")));
        }

        Assert.True(File.Exists(Data.DatabasePath));
        var log = Assert.Single(Directory.GetFiles(Data.LogsDirectory, "pairsync-*.log"));
        Assert.Contains("PairSync core started", await ReadSharedAsync(log));
    }

    [Fact]
    public async Task Data_survives_a_restart()
    {
        var deviceId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        Guid identityId;
        await using (var core = await PairSyncCore.StartAsync(Data, Ct))
        {
            identityId = core.Identity.Identity.Id;
            var contexts = core.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(Ct))
            {
                db.Devices.Add(new PairedDevice
                {
                    Id = deviceId,
                    Name = "Workstation",
                    PublicKey = [1, 2, 3],
                    PairedAtUtc = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc),
                });
                await db.SaveChangesAsync(Ct);
            }
            await core.Services.GetRequiredService<IChunkJournal>().SaveAsync(
                new ChunkJournalEntry { TransferId = transferId, TempPath = "x.pairsync-tmp", FileSize = 10, ChunkSize = 4, ChunkCount = 3, Confirmed = [0b101] },
                Ct);
            core.Settings.Update(s => s with { DeviceName = "Studio", Port = 48000 });
        }

        await using (var core = await PairSyncCore.StartAsync(Data, Ct))
        {
            var contexts = core.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>();
            await using var db = await contexts.CreateDbContextAsync(Ct);
            var device = await db.Devices.SingleAsync(Ct);
            Assert.Equal(deviceId, device.Id);
            Assert.Equal(DateTimeKind.Utc, device.PairedAtUtc.Kind);
            Assert.Equal(new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc), device.PairedAtUtc);

            var journal = await core.Services.GetRequiredService<IChunkJournal>().LoadAsync(transferId, Ct);
            Assert.NotNull(journal);
            Assert.Equal([0b101], journal.Confirmed);

            Assert.Equal(identityId, core.Identity.Identity.Id);
            Assert.Equal("Studio", core.Settings.Current.DeviceName);
            Assert.Equal(48000, core.Settings.Current.Port);
        }
    }

    [Fact]
    public async Task Journal_entries_are_replaced_and_deleted()
    {
        await using var core = await PairSyncCore.StartAsync(Data, Ct);
        var journal = core.Services.GetRequiredService<IChunkJournal>();
        var entry = new ChunkJournalEntry { TransferId = Guid.NewGuid(), TempPath = "a", FileSize = 1, ChunkSize = 1, ChunkCount = 9, Confirmed = [1, 0] };

        await journal.SaveAsync(entry, Ct);
        await journal.SaveAsync(entry with { Confirmed = [0xFF, 1] }, Ct);
        Assert.Equal([0xFF, 1], (await journal.LoadAsync(entry.TransferId, Ct))!.Confirmed);

        await journal.DeleteAsync(entry.TransferId, Ct);
        Assert.Null(await journal.LoadAsync(entry.TransferId, Ct));
        await journal.DeleteAsync(entry.TransferId, Ct); // deleting twice is fine
    }

    /// <summary>The log file stays open while Serilog runs; read it without asking for exclusive access.</summary>
    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }
}
