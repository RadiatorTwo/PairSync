using System.Security.Cryptography;
using PairSync.Application.Sync;
using PairSync.Domain;
using PairSync.Protocol;

namespace PairSync.UnitTests.Sync;

public sealed class FolderScannerTests : IDisposable
{
    private static readonly Guid Me = Guid.NewGuid();
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-scan-");
    private readonly FolderScanner _scanner = new(Me, TimeProvider.System);
    private readonly Dictionary<string, SyncFile> _index = new(StringComparer.Ordinal);

    public void Dispose() => _root.Delete(recursive: true);

    private string Write(string path, string content, int ageSeconds = 60)
    {
        var full = Path.Combine(_root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(-ageSeconds));
        return full;
    }

    private ScanResult ScanAndApply(ExcludeRules? excludes = null)
    {
        var result = _scanner.Scan(_root.FullName, excludes ?? ExcludeRules.None, _index, CancellationToken.None);
        foreach (var entry in result.Changes.Concat(result.Touched))
            _index[entry.Path] = entry;
        return result;
    }

    [Fact]
    public void New_files_and_folders_get_a_version_and_hashes()
    {
        Write("docs/a.txt", "hello");

        var result = ScanAndApply();

        Assert.Equal(["docs", "docs/a.txt"], result.Changes.Select(c => c.Path).Order());
        var file = _index["docs/a.txt"];
        Assert.Equal(5, file.Size);
        Assert.Equal(SHA256.HashData("hello"u8), file.Sha256);
        Assert.Equal(32, file.ChunkHashes!.Length);
        Assert.Equal(1, file.VersionVector[Me]);
    }

    [Fact]
    public void Unchanged_files_are_not_reported_and_changes_raise_the_version()
    {
        var full = Write("a.txt", "one");
        ScanAndApply();

        Assert.Empty(ScanAndApply().Changes);

        Write("a.txt", "two!");
        var changed = ScanAndApply();
        Assert.Equal(2, Assert.Single(changed.Changes).VersionVector[Me]);

        // Same content, new time: only the time hint is updated.
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(-30));
        var touched = ScanAndApply();
        Assert.Empty(touched.Changes);
        Assert.Single(touched.Touched);
    }

    [Fact]
    public void Missing_paths_become_tombstones_and_a_new_file_follows_the_tombstone()
    {
        var full = Write("a.txt", "one");
        ScanAndApply();
        File.Delete(full);

        var deleted = Assert.Single(ScanAndApply().Changes);
        Assert.True(deleted.Deleted);
        Assert.Equal(2, deleted.VersionVector[Me]);

        Write("a.txt", "again");
        var recreated = Assert.Single(ScanAndApply().Changes);
        Assert.False(recreated.Deleted);
        Assert.Equal(3, recreated.VersionVector[Me]);
    }

    [Fact]
    public void Excluded_busy_and_metadata_paths_are_left_out()
    {
        Write("keep.txt", "x");
        Write("skip.tmp", "x");
        Write(".pairsync/trash/old.txt", "x");
        Write("big.bin.pairsync-tmp", "x");
        Write("fresh.txt", "just written", ageSeconds: 0);

        var result = ScanAndApply(ExcludeRules.Parse("*.tmp"));

        Assert.Equal(["keep.txt"], result.Changes.Select(c => c.Path));
        Assert.True(result.Busy);
    }

    [Fact]
    public void Missing_root_does_not_delete_anything()
    {
        Write("a.txt", "x");
        ScanAndApply();

        Assert.Throws<SyncFolderUnavailableException>(() =>
            _scanner.Scan(Path.Combine(_root.FullName, "missing"), ExcludeRules.None, _index, CancellationToken.None));
    }

    [Fact]
    public void Chunk_hashes_cover_every_4_mib()
    {
        var full = Path.Combine(_root.FullName, "big.bin");
        var data = new byte[ProtocolLimits.ChunkSize * 2 + 10];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(full, data);

        var hashes = FileHashes.Compute(full, CancellationToken.None);

        Assert.Equal(3 * 32, hashes.ChunkHashes.Length);
        Assert.Equal(SHA256.HashData(data), hashes.Sha256);
        Assert.Equal(FileHashes.ChunkCount(data.Length), hashes.ChunkHashes.Length / 32);
    }
}
