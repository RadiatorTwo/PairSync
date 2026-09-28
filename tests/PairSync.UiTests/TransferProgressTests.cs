using PairSync.Application.Transfers;
using PairSync.Desktop.ViewModels;
using PairSync.Domain;

namespace PairSync.UiTests;

public sealed class TransferProgressTests(HeadlessFixture ui)
{
    private const long MB = 1024 * 1024;

    private static JobView Job(int files, long total, long transferred, int filesDone, string? current = null, long currentSize = 0, long currentBytes = 0) =>
        new(Guid.NewGuid(), TransferDirection.Send, Guid.NewGuid(), "office-pc", JobState.Running, false, files, total, transferred,
            current, 0, 0, 0, 0, null, DateTime.UtcNow)
        {
            Title = "Projects",
            FilesDone = filesDone,
            CurrentFileSize = currentSize,
            CurrentFileBytes = currentBytes,
        };

    [Fact]
    public Task Many_files_move_the_bar_by_count_not_by_bytes() => ui.RunAsync(() =>
    {
        var row = new TransferRowViewModel(Guid.NewGuid(), null!);

        // 300 of 600 small files through, but only 1% of the bytes: the large ones come last.
        row.Update(Job(600, 1000 * MB, 10 * MB, 300, "src/a.cs", 2000, 0));

        Assert.Equal(0.5, row.Fraction, 3);
        Assert.Null(row.FileBar);
        Assert.Equal("300 / 600 files · src/a.cs", row.Meta);
    });

    [Fact]
    public Task A_large_file_gets_its_own_bar() => ui.RunAsync(() =>
    {
        var row = new TransferRowViewModel(Guid.NewGuid(), null!);

        row.Update(Job(600, 1000 * MB, 500 * MB, 590, "video.mkv", 400 * MB, 100 * MB));

        Assert.NotNull(row.FileBar);
        Assert.Equal("video.mkv", row.FileBar.Name);
        Assert.Equal(0.25, row.FileBar.Fraction, 3);
        Assert.Equal("590 / 600 files", row.Meta);
    });

    [Fact]
    public Task A_single_file_keeps_the_byte_bar() => ui.RunAsync(() =>
    {
        var row = new TransferRowViewModel(Guid.NewGuid(), null!);

        row.Update(Job(1, 400 * MB, 100 * MB, 0, "video.mkv", 400 * MB, 100 * MB));

        Assert.Equal(0.25, row.Fraction, 3);
        Assert.Null(row.FileBar);
    });
}
