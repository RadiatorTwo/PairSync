using System.Text;
using PairSync.Application.Sync;

namespace PairSync.UnitTests.Sync;

public sealed class SyncPathsTests
{
    [Fact]
    public void Paths_use_forward_slashes_and_nfc()
    {
        const string decomposed = "Müller/a.txt"; // as macOS stores it
        Assert.Equal("Müller/a.txt", SyncPaths.Normalize(decomposed));
        Assert.Equal("a/b/c.txt", SyncPaths.Normalize(@"a\b\c.txt"));
    }

    [Fact]
    public void Random_paths_normalize_idempotently_and_valid_ones_stay_inside_the_root()
    {
        var random = new Random(4711);
        const string alphabet = "abcXYZ019 ._-/\\:*?<>|äöüé́̈";
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pairsync-root"));
        for (var i = 0; i < 5000; i++)
        {
            var text = new StringBuilder();
            var length = random.Next(1, 40);
            for (var j = 0; j < length; j++)
                text.Append(alphabet[random.Next(alphabet.Length)]);
            var normalized = SyncPaths.Normalize(text.ToString());

            Assert.Equal(normalized, SyncPaths.Normalize(normalized));
            Assert.DoesNotContain('\\', normalized);
            if (SyncPaths.Check(normalized) is null)
                Assert.StartsWith(root + Path.DirectorySeparatorChar, SyncPaths.Full(root, normalized), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("CON.txt")]
    [InlineData("a:b")]
    [InlineData("name.")]
    public void Unsafe_or_windows_invalid_paths_are_refused(string path) => Assert.NotNull(SyncPaths.Check(path));
}
