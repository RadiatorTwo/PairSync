using PairSync.Application.Sync;
using PairSync.Domain;

namespace PairSync.UnitTests.Sync;

public sealed class SyncPlannerTests
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static VersionVector V(long me, long other) => VersionVector.From([new(Me, me), new(Other, other)]);

    private static SyncFile File(VersionVector version, string content = "a", string path = "f.txt") => new()
    {
        Path = path,
        Size = content.Length,
        Sha256 = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)),
        Version = version.ToString(),
    };

    private static SyncFile Tombstone(VersionVector version) => new() { Path = "f.txt", Deleted = true, Version = version.ToString() };

    private static SyncFile Folder(VersionVector version) => new() { Path = "f.txt", IsDirectory = true, Version = version.ToString() };

    private static SyncProfile Profile(SyncDirection direction = SyncDirection.TwoWay, bool write = true, bool delete = true) =>
        new() { Direction = direction, AllowWrite = write, AllowDelete = delete };

    private static SyncActionKind Kind(SyncFile? local, SyncFile? remote, SyncProfile? profile = null) =>
        SyncPlanner.Plan(profile ?? Profile(), Me, local, remote).Kind;

    [Fact]
    public void Unknown_path_is_fetched_or_created_and_unknown_tombstone_ignored()
    {
        Assert.Equal(SyncActionKind.Fetch, Kind(null, File(V(0, 1))));
        Assert.Equal(SyncActionKind.CreateDirectory, Kind(null, Folder(V(0, 1))));
        Assert.Equal(SyncActionKind.None, Kind(null, Tombstone(V(0, 2))));
    }

    [Fact]
    public void Newer_remote_version_is_taken_older_or_equal_is_ignored()
    {
        Assert.Equal(SyncActionKind.Fetch, Kind(File(V(1, 1)), File(V(1, 2), "b")));
        Assert.Equal(SyncActionKind.None, Kind(File(V(2, 1)), File(V(1, 1), "b")));
        Assert.Equal(SyncActionKind.None, Kind(File(V(1, 1)), File(V(1, 1))));
        Assert.Equal(SyncActionKind.Delete, Kind(File(V(1, 1)), Tombstone(V(1, 2))));
        Assert.Equal(SyncActionKind.AdoptVersion, Kind(Tombstone(V(1, 1)), Tombstone(V(1, 2))));
    }

    [Fact]
    public void Same_content_only_takes_over_the_version()
    {
        var action = SyncPlanner.Plan(Profile(), Me, File(V(2, 0)), File(V(0, 1)));

        Assert.Equal(SyncActionKind.AdoptVersion, action.Kind);
        Assert.Equal(V(2, 1).ToString(), action.Version);
        Assert.Equal(SyncActionKind.AdoptVersion, Kind(File(V(1, 1)), File(V(1, 2))));
        Assert.Equal(SyncActionKind.AdoptVersion, Kind(Folder(V(2, 0)), Folder(V(0, 1))));
    }

    [Fact]
    public void Independent_changes_with_different_content_are_a_conflict()
    {
        var action = SyncPlanner.Plan(Profile(), Me, File(V(2, 1), "mine"), File(V(1, 2), "theirs"));

        Assert.Equal(SyncActionKind.Conflict, action.Kind);
        Assert.Equal(V(2, 2).ToString(), action.Version);
    }

    [Fact]
    public void Change_beats_deletion_on_both_sides()
    {
        var keep = SyncPlanner.Plan(Profile(), Me, File(V(2, 1)), Tombstone(V(1, 2)));
        Assert.Equal(SyncActionKind.KeepLocal, keep.Kind);
        Assert.Equal(VersionOrder.Newer, VersionVector.Parse(keep.Version).Compare(V(1, 2)));
        Assert.NotNull(keep.Note);

        var restore = SyncPlanner.Plan(Profile(), Me, Tombstone(V(2, 1)), File(V(1, 2)));
        Assert.Equal(SyncActionKind.Fetch, restore.Kind);
        Assert.NotNull(restore.Note);
    }

    [Fact]
    public void Direction_and_rights_are_respected()
    {
        Assert.Equal(SyncActionKind.None, Kind(null, File(V(0, 1)), Profile(SyncDirection.SendOnly)));
        Assert.Equal(SyncActionKind.Fetch, Kind(null, File(V(0, 1)), Profile(SyncDirection.ReceiveOnly)));
        Assert.Equal(SyncActionKind.None, Kind(null, File(V(0, 1)), Profile(write: false)));
        Assert.Equal(SyncActionKind.None, Kind(File(V(1, 1)), File(V(1, 2), "b"), Profile(write: false)));
        Assert.Equal(SyncActionKind.None, Kind(File(V(1, 1)), Tombstone(V(1, 2)), Profile(delete: false)));
        Assert.Equal(SyncActionKind.Delete, Kind(File(V(1, 1)), Tombstone(V(1, 2)), Profile(write: false)));
    }

    [Fact]
    public void Conflict_copy_name_is_the_same_on_both_devices()
    {
        var time = new DateTime(2026, 9, 26, 15, 42, 10, DateTimeKind.Utc);

        Assert.True(ConflictNames.LocalRenames(Me, Other));
        Assert.False(ConflictNames.LocalRenames(Other, Me));
        Assert.Equal("docs/plan (conflict 111111 2026-09-26 154210).md", ConflictNames.CopyPath("docs/plan.md", Me, time));
        Assert.Equal("Makefile (conflict 111111 2026-09-26 154210)", ConflictNames.CopyPath("Makefile", Me, time));
        Assert.Equal(".env (conflict 111111 2026-09-26 154210)", ConflictNames.CopyPath(".env", Me, time));
    }
}
