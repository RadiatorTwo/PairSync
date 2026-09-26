using PairSync.Application.Sync;

namespace PairSync.UnitTests.Sync;

public sealed class ExcludeRulesTests
{
    [Theory]
    [InlineData("*.tmp", "a.tmp", false, true)]
    [InlineData("*.tmp", "deep/down/a.tmp", false, true)]
    [InlineData("*.tmp", "a.tmp.txt", false, false)]
    [InlineData("node_modules/", "node_modules", true, true)]
    [InlineData("node_modules/", "src/node_modules", true, true)]
    [InlineData("node_modules/", "node_modules", false, false)]
    [InlineData("/build", "build", true, true)]
    [InlineData("/build", "src/build", true, false)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]
    [InlineData("docs/*.md", "docs/sub/a.md", false, false)]
    [InlineData("docs/**/*.md", "docs/sub/deeper/a.md", false, true)]
    [InlineData("docs/**/*.md", "docs/a.md", false, true)]
    [InlineData("**/cache", "a/b/cache", true, true)]
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("file?.txt", "file12.txt", false, false)]
    public void Pattern_matches_like_gitignore(string pattern, string path, bool isDirectory, bool excluded) =>
        Assert.Equal(excluded, ExcludeRules.Parse(pattern).IsExcluded(path, isDirectory));

    [Fact]
    public void Last_matching_line_wins_and_comments_are_ignored()
    {
        var rules = ExcludeRules.Parse("# logs\n*.log\n!keep.log\n\n");

        Assert.True(rules.IsExcluded("a.log", false));
        Assert.False(rules.IsExcluded("keep.log", false));
    }

    [Fact]
    public void Metadata_and_transfer_files_are_always_excluded()
    {
        Assert.True(ExcludeRules.None.IsExcluded(".pairsync", true));
        Assert.True(ExcludeRules.None.IsExcluded(".pairsync/trash/x", false));
        Assert.True(ExcludeRules.None.IsExcluded("big.iso.pairsync-tmp", false));
        Assert.False(ExcludeRules.None.IsExcluded(".pairsyncrc", false));
    }

    [Fact]
    public void Paths_below_an_excluded_folder_are_excluded()
    {
        var rules = ExcludeRules.Parse("bin/");

        Assert.False(rules.IsExcluded("bin/app.dll", false));
        Assert.True(rules.IsExcludedWithParents("bin/app.dll", false));
        Assert.True(rules.IsExcludedWithParents("src/bin/x/app.dll", false));
    }
}
