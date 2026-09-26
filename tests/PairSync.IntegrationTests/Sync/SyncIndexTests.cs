using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Sync;
using PairSync.Domain;
using PairSync.Storage;

namespace PairSync.IntegrationTests.Sync;

public sealed class SyncIndexTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-index-");
    private PairSyncCore _core = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _core = await TestCores.StartAsync(new DataDirectory(_root.FullName), Ct);

    public async ValueTask DisposeAsync()
    {
        await _core.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private SyncIndex Index => _core.Services.GetRequiredService<SyncIndex>();

    private async Task<Guid> AddProfileAsync()
    {
        var profile = new SyncProfile { Id = Guid.NewGuid(), Name = "Projects", LocalPath = _root.FullName, IndexId = Guid.NewGuid(), State = SyncProfileState.Active };
        await using var db = await _core.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync(Ct);
        db.SyncProfiles.Add(profile);
        await db.SaveChangesAsync(Ct);
        return profile.Id;
    }

    [Fact]
    public async Task Local_changes_get_increasing_sequences_and_can_be_read_after_a_sequence()
    {
        var id = await AddProfileAsync();

        await Index.RecordLocalAsync(id, [new SyncFile { Path = "a.txt", Size = 1 }, new SyncFile { Path = "b.txt", Size = 2 }], Ct);
        var last = await Index.RecordLocalAsync(id, [new SyncFile { Path = "a.txt", Size = 3 }], Ct);

        Assert.Equal(3, last);
        var changes = await Index.LocalChangesSinceAsync(id, 1, 100, Ct);
        Assert.Equal(["b.txt", "a.txt"], changes.Select(c => c.Path));
        Assert.Equal(3, changes[1].Size);
        Assert.Equal(2, (await Index.LoadAsync(id, SyncSide.Local, Ct)).Count);
    }

    [Fact]
    public async Task Remote_entries_are_replaced_when_the_other_index_is_new()
    {
        var id = await AddProfileAsync();
        var first = Guid.NewGuid();
        await Index.RecordRemoteAsync(id, first, [new SyncFile { Path = "old.txt", Sequence = 7 }], 7, Ct);

        await Index.RecordRemoteAsync(id, Guid.NewGuid(), [new SyncFile { Path = "new.txt", Sequence = 1 }], 1, Ct);

        var remote = await Index.LoadAsync(id, SyncSide.Remote, Ct);
        Assert.Equal(["new.txt"], remote.Keys);
    }

    [Fact]
    public async Task Old_tombstones_are_purged()
    {
        var id = await AddProfileAsync();
        await Index.RecordLocalAsync(id,
        [
            new SyncFile { Path = "gone.txt", Deleted = true, DeletedAtUtc = DateTime.UtcNow.AddDays(-31) },
            new SyncFile { Path = "recent.txt", Deleted = true, DeletedAtUtc = DateTime.UtcNow },
        ], Ct);

        Assert.Equal(1, await Index.PurgeTombstonesAsync(id, DateTime.UtcNow.AddDays(-30), Ct));
        Assert.Equal(["recent.txt"], (await Index.LoadAsync(id, SyncSide.Local, Ct)).Keys);
    }
}
