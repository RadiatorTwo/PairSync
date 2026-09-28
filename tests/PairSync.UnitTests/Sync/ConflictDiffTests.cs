using System.Text;
using PairSync.Application.Sync;
using PairSync.Domain;

namespace PairSync.UnitTests.Sync;

public sealed class ConflictDiffTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pairsync-diff-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Only_the_differing_lines_are_shown()
    {
        var result = ConflictDiff.Compare("a\nb\nc\nd\ne\n", "a\nB\nc\nd\ne\nf\n");

        Assert.Equal(ConflictComparisonKind.Text, result.Kind);
        Assert.Collection(result.Hunks,
            h =>
            {
                Assert.Equal((2, 2), (h.LocalStart, h.RemoteStart));
                Assert.Equal(["b"], h.LocalLines);
                Assert.Equal(["B"], h.RemoteLines);
            },
            h =>
            {
                Assert.Empty(h.LocalLines);
                Assert.Equal(5, h.LocalStart); // after line 5
                Assert.Equal(6, h.RemoteStart);
                Assert.Equal(["f"], h.RemoteLines);
            });
    }

    [Fact]
    public void Inserted_block_in_the_middle_is_one_place()
    {
        var local = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var remote = local.Replace("line 50\n", "line 50\nnew 1\nnew 2\n", StringComparison.Ordinal);

        var hunk = Assert.Single(ConflictDiff.Compare(local, remote).Hunks);

        Assert.Empty(hunk.LocalLines);
        Assert.Equal(["new 1", "new 2"], hunk.RemoteLines);
        Assert.Equal(51, hunk.RemoteStart);
    }

    [Fact]
    public void Line_endings_alone_are_named()
    {
        var result = ConflictDiff.Compare("a\r\nb\r\n", "a\nb\n");

        Assert.Equal(ConflictComparisonKind.LineEndingsOnly, result.Kind);
        Assert.Empty(result.Hunks);
    }

    [Fact]
    public void Diff_is_minimal_and_complete_for_random_texts()
    {
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 30)).Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray();
            var b = Enumerable.Range(0, random.Next(0, 30)).Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray();

            var result = ConflictDiff.Compare(string.Join('\n', a), string.Join('\n', b));

            // Applying the hunks to a must give b.
            var rebuilt = new List<string>();
            var at = 0;
            foreach (var hunk in result.Hunks)
            {
                var start = hunk.LocalLines.Count > 0 ? hunk.LocalStart - 1 : hunk.LocalStart;
                rebuilt.AddRange(a[at..start]);
                rebuilt.AddRange(hunk.RemoteLines);
                at = start + hunk.LocalLines.Count;
            }
            rebuilt.AddRange(a[at..]);
            Assert.Equal(b, rebuilt);
            // Shortest edit script: changed lines = |a| + |b| - 2 * LCS.
            var changed = result.Hunks.Sum(h => h.LocalLines.Count + h.RemoteLines.Count);
            Assert.Equal(a.Length + b.Length - 2 * Lcs(a, b), changed);
        }
    }

    [Fact]
    public void Too_many_changes_give_up()
    {
        var local = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"a{i}"));
        var remote = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"b{i}"));

        Assert.Equal(ConflictComparisonKind.TooManyChanges, ConflictDiff.Compare(local, remote).Kind);
    }

    [Fact]
    public async Task Files_are_read_with_the_local_version_on_the_left()
    {
        File.WriteAllText(Path.Combine(_root, "plan.md"), "remote\n");
        File.WriteAllText(Path.Combine(_root, "plan (conflict).md"), "local\n");
        var conflict = new SyncConflict { Path = "plan.md", CopyPath = "plan (conflict).md", CopyIsLocal = true };

        var hunk = Assert.Single((await ConflictDiff.CompareAsync(_root, conflict, CancellationToken.None)).Hunks);

        Assert.Equal(["local"], hunk.LocalLines);
        Assert.Equal(["remote"], hunk.RemoteLines);
    }

    [Fact]
    public async Task Binary_large_and_missing_files_are_not_compared()
    {
        var conflict = new SyncConflict { Path = "a.bin", CopyPath = "b.bin" };
        Assert.Equal(ConflictComparisonKind.Missing, (await ConflictDiff.CompareAsync(_root, conflict, CancellationToken.None)).Kind);

        File.WriteAllBytes(Path.Combine(_root, "a.bin"), [1, 0, 2]);
        File.WriteAllText(Path.Combine(_root, "b.bin"), "text");
        Assert.Equal(ConflictComparisonKind.Binary, (await ConflictDiff.CompareAsync(_root, conflict, CancellationToken.None)).Kind);

        File.WriteAllBytes(Path.Combine(_root, "a.bin"), Encoding.UTF8.GetBytes(new string('x', (int)ConflictDiff.MaxFileSize + 1)));
        Assert.Equal(ConflictComparisonKind.TooLarge, (await ConflictDiff.CompareAsync(_root, conflict, CancellationToken.None)).Kind);
    }

    private static int Lcs(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
        }
        return table[a.Length, b.Length];
    }
}
