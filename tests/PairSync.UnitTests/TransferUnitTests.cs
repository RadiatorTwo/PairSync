using System.Diagnostics;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.SyncEngine;

namespace PairSync.UnitTests;

public sealed class RelativePathTests
{
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("Photos/2024/Urlaub am Meer.jpg")]
    [InlineData("Ordner mit Ümlauten/日本語.txt")]
    [InlineData(".hidden/.config")]
    [InlineData("CONSOLE.txt")]
    [InlineData("a/b/c/d/e")]
    public void Normal_paths_are_accepted(string path)
    {
        Assert.Null(RelativePaths.Check(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("../outside.txt")]
    [InlineData("a/../../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:x")]
    [InlineData("a\\b")]
    [InlineData("\\\\server\\share")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("folder/NUL.tar.gz")]
    [InlineData("LPT1")]
    [InlineData("COM9.log")]
    [InlineData("trailing ")]
    [InlineData("trailing.")]
    [InlineData("bad|name")]
    [InlineData("what?")]
    [InlineData("tab\tname")]
    [InlineData("nul\0byte")]
    public void Unsafe_paths_are_rejected(string path)
    {
        Assert.NotNull(RelativePaths.Check(path));
    }

    [Fact]
    public void Overlong_names_and_paths_are_rejected()
    {
        Assert.NotNull(RelativePaths.Check(new string('a', RelativePaths.MaxSegmentLength + 1)));
        Assert.NotNull(RelativePaths.Check(string.Join('/', Enumerable.Repeat(new string('a', 200), 6))));
    }

    [Fact]
    public void Random_paths_that_pass_the_check_always_stay_inside_the_root()
    {
        // Property-based (plan §14): generated from the characters that matter for traversal and Windows names.
        var alphabet = new[] { "a", "b", ".", "..", "/", "\\", ":", " ", "C", "CON", "nul", "~", "\0", "é", "*", "COM1" };
        var random = new Random(4827_1930);
        var root = Path.Combine(Path.GetTempPath(), "pairsync-root");
        var accepted = 0;
        for (var n = 0; n < 50_000; n++)
        {
            var path = string.Concat(Enumerable.Range(0, random.Next(1, 9)).Select(_ => alphabet[random.Next(alphabet.Length)]));
            if (!RelativePaths.IsValid(path))
                continue;
            accepted++;
            var full = RelativePaths.Combine(root, path);
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, full);
            Assert.DoesNotContain("..", path.Split('/'));
        }
        Assert.True(accepted > 1000, $"only {accepted} generated paths were valid");
    }

    [Theory]
    [InlineData("a.txt", "A.TXT")]
    [InlineData("Photos/x.jpg", "photos/y.jpg")]
    [InlineData("data", "data/inner.bin")]
    [InlineData("same", "same")]
    public void Paths_that_clash_on_a_case_insensitive_disk_are_found(string first, string second)
    {
        Assert.NotNull(RelativePaths.FindCaseCollision([(first, false), (second, false)]));
    }

    [Fact]
    public void Distinct_paths_and_shared_folders_do_not_clash()
    {
        Assert.Null(RelativePaths.FindCaseCollision([("Photos/a.jpg", false), ("Photos/b.jpg", false), ("Photos/empty", true), ("x", false)]));
        Assert.NotNull(RelativePaths.FindCaseCollision([("Photos", true), ("photos/a.jpg", false)]));
    }
}

public sealed class UploadThrottleTests
{
    [Fact]
    public async Task Unlimited_does_not_wait()
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
            await UploadThrottle.Unlimited.WaitAsync(256 * 1024, TestContext.Current.CancellationToken);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Limit_spreads_the_bytes_over_time()
    {
        const long rate = 2 * 1024 * 1024;
        var throttle = new UploadThrottle(() => rate, TimeProvider.System);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 16; i++)
            await throttle.WaitAsync(256 * 1024, TestContext.Current.CancellationToken); // 4 MiB at 2 MiB/s

        Assert.InRange(watch.Elapsed.TotalSeconds, 1.7, 4);
    }

    [Fact]
    public async Task Changing_the_setting_takes_effect_immediately()
    {
        long rate = 64 * 1024;
        var throttle = new UploadThrottle(() => rate, TimeProvider.System);
        await throttle.WaitAsync(64 * 1024, TestContext.Current.CancellationToken);

        rate = 0;
        var watch = Stopwatch.StartNew();
        await throttle.WaitAsync(64 * 1024 * 1024, TestContext.Current.CancellationToken);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(200));
    }
}

public sealed class SendScannerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-scan-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Create(string relative, int size = 10)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void Folder_is_flattened_with_relative_paths_and_empty_folders()
    {
        Create("Photos/2024/a.jpg", 100);
        Create("Photos/b.jpg", 50);
        Directory.CreateDirectory(Path.Combine(_root.FullName, "Photos/empty"));
        var single = Create("notes.txt", 7);

        var draft = SendScanner.Scan([Path.Combine(_root.FullName, "Photos"), single], TestContext.Current.CancellationToken);

        Assert.Empty(draft.Problems);
        Assert.Equal(["Photos/2024/a.jpg", "Photos/b.jpg", "Photos/empty", "notes.txt"], draft.Items.Select(i => i.RelativePath).Order(StringComparer.Ordinal));
        Assert.True(draft.Items.Single(i => i.RelativePath == "Photos/empty").IsDirectory);
        Assert.Equal(3, draft.FileCount);
        Assert.Equal(157, draft.TotalBytes);
        var photos = draft.Entries.Single(e => e.Name == "Photos");
        Assert.True(photos.IsFolder);
        Assert.Equal((150L, 2), (photos.Size, photos.FileCount));
    }

    [Fact]
    public void Same_name_picked_twice_gets_a_number()
    {
        var first = Create("one/report.pdf");
        var second = Create("two/report.pdf");

        var draft = SendScanner.Scan([first, second], TestContext.Current.CancellationToken);

        Assert.Equal(["report.pdf", "report (2).pdf"], draft.Items.Select(i => i.RelativePath));
    }

    [Fact]
    public void Links_pointing_outside_the_folder_are_skipped()
    {
        var outside = Create("elsewhere/secret.txt");
        var inside = Create("Share/real.txt");
        var linkOut = Path.Combine(_root.FullName, "Share", "out.txt");
        var linkIn = Path.Combine(_root.FullName, "Share", "in.txt");
        try
        {
            File.CreateSymbolicLink(linkOut, outside);
            File.CreateSymbolicLink(linkIn, inside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links needs developer mode or admin rights on Windows.");
        }

        var draft = SendScanner.Scan([Path.Combine(_root.FullName, "Share")], TestContext.Current.CancellationToken);

        Assert.Contains(linkOut, draft.SkippedLinks);
        Assert.Contains(draft.Items, i => i.RelativePath == "Share/in.txt");
        Assert.DoesNotContain(draft.Items, i => i.RelativePath == "Share/out.txt");
    }

    [Fact]
    public void Missing_path_is_reported()
    {
        var draft = SendScanner.Scan([Path.Combine(_root.FullName, "nope")], TestContext.Current.CancellationToken);
        Assert.Single(draft.Problems);
        Assert.Empty(draft.Items);
    }
}

public sealed class JobTitleTests
{
    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "Photos-2026.tar" }, "Photos-2026.tar")]
    [InlineData(new[] { "Photos/a.jpg", "Photos/b/c.jpg" }, "Photos")]
    [InlineData(new[] { "Photos/a.jpg", "notes.md", "Photos/b.jpg", "docs/x" }, "Photos +2")]
    public void Title_names_the_first_top_level_entry(string[] paths, string expected) =>
        Assert.Equal(expected, JobTitles.From(paths));
}
