using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.SyncEngine;

namespace PairSync.IntegrationTests;

/// <summary>Two paired cores on loopback: jobs with confirmation, policies, pause/cancel, interruption and resume.</summary>
public sealed class TransferTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-transfer-");
    private readonly List<PairSyncCore> _cores = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var core in _cores)
            await core.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private DataDirectory DataFor(string name) => new(Path.Combine(_root.FullName, name));

    private async Task<PairSyncCore> StartAsync(string name, TransferOptions? transfers = null)
    {
        var data = DataFor(name);
        var core = await TestCores.StartAsync(data, Ct, transfers: transfers);
        core.Settings.Update(s => s with { DeviceName = name });
        _cores.Add(core);
        return core;
    }

    private async Task<(PairSyncCore Sender, PairSyncCore Receiver)> StartPairAsync(TransferOptions? receiverOptions = null)
    {
        var sender = await StartAsync("laptop");
        var receiver = await StartAsync("workstation", receiverOptions);
        await TestCores.PairAsync(sender, receiver, Ct);
        await sender.Presence.ReloadPairedDevicesAsync(Ct);
        await receiver.Presence.ReloadPairedDevicesAsync(Ct);
        return (sender, receiver);
    }

    private string Source(string relative, int size)
    {
        var path = Path.Combine(_root.FullName, "source", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new byte[size];
        RandomNumberGenerator.Fill(content);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Where the receiver puts files from "laptop": <c>Downloads/PairSync/laptop</c> of its data directory.</summary>
    private static string TargetOf(PairSyncCore receiver, string sender = "laptop") =>
        Path.Combine(receiver.DataDirectory.Root, "downloads", "PairSync", sender);

    private static async Task<HistoryEntry> WaitForHistoryAsync(PairSyncCore core, Guid jobId, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if ((await core.Transfers.GetHistoryAsync(50, Ct)).FirstOrDefault(h => h.JobId == jobId) is { } entry)
                return entry;
            await Task.Delay(100, Ct);
        }
        var jobs = await core.Transfers.GetJobsAsync(Ct);
        throw new TimeoutException($"Job {jobId} did not finish; jobs: {string.Join("; ", jobs)}");
    }

    private static async Task<JobView> WaitForJobAsync(PairSyncCore core, Guid jobId, Func<JobView, bool> condition, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JobView? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = (await core.Transfers.GetJobsAsync(Ct)).FirstOrDefault(j => j.Id == jobId);
            if (last is not null && condition(last))
                return last;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"Job {jobId} did not reach the expected state; last: {last}");
    }

    private static async Task<List<JobItem>> ItemsOfAsync(PairSyncCore core, Guid jobId)
    {
        await using var db = await core.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync(Ct);
        return await db.JobItems.AsNoTracking().Where(i => i.JobId == jobId).ToListAsync(Ct);
    }

    private Task<Guid> SendAsync(PairSyncCore from, PairSyncCore to, IEnumerable<string> paths, ExistingFilePolicy policy = ExistingFilePolicy.KeepBoth) =>
        from.Transfers.SendAsync(to.Identity.Identity.Id, SendScanner.Scan(paths, Ct), policy, suggestedFolder: null, Ct);

    [Fact]
    public async Task Folder_arrives_without_asking_the_receiver()
    {
        var (sender, receiver) = await StartPairAsync();
        var big = Source("Project/data/big.bin", 3 * ProtocolLimits.ChunkSize + 12345);
        var small = Source("Project/readme.md", 42);
        Directory.CreateDirectory(Path.Combine(_root.FullName, "source", "Project", "empty"));
        File.SetLastWriteTimeUtc(small, new DateTime(2024, 5, 1, 8, 30, 0, DateTimeKind.Utc));
        TestCores.MakeReachable(sender, receiver);

        var jobId = await SendAsync(sender, receiver, [Path.Combine(_root.FullName, "source", "Project")]);
        var sent = await WaitForHistoryAsync(sender, jobId);
        var received = await WaitForHistoryAsync(receiver, jobId);

        Assert.Equal(HistoryOutcome.Completed, sent.Outcome);
        Assert.Equal(HistoryOutcome.Completed, received.Outcome);
        Assert.Equal("workstation", sent.PeerName);
        Assert.Equal("laptop", received.PeerName);
        Assert.Equal(TransferDirection.Receive, received.Direction);
        Assert.Equal(2, received.FileCount);
        Assert.Equal(new FileInfo(big).Length + 42, received.TotalBytes);
        var target = Path.Combine(TargetOf(receiver), "Project");
        Assert.Equal(await File.ReadAllBytesAsync(big, Ct), await File.ReadAllBytesAsync(Path.Combine(target, "data", "big.bin"), Ct));
        Assert.Equal(new DateTime(2024, 5, 1, 8, 30, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(target, "readme.md")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.Empty(Directory.GetFiles(TargetOf(receiver), "*.pairsync-tmp", SearchOption.AllDirectories));
        Assert.Empty(await sender.Transfers.GetJobsAsync(Ct));
    }

    [Fact]
    public async Task Suggested_subfolder_is_used_below_the_sender_folder()
    {
        var (sender, receiver) = await StartPairAsync();
        TestCores.MakeReachable(sender, receiver);

        var jobId = await sender.Transfers.SendAsync(receiver.Identity.Identity.Id, SendScanner.Scan([Source("a.txt", 10)], Ct),
            ExistingFilePolicy.KeepBoth, "Holiday/2026", Ct);

        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(receiver, jobId)).Outcome);
        Assert.True(File.Exists(Path.Combine(TargetOf(receiver), "Holiday", "2026", "a.txt")));
    }

    [Fact]
    public async Task Device_that_may_not_send_is_declined()
    {
        var (sender, receiver) = await StartPairAsync();
        await using (var db = await receiver.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync(Ct))
            await db.Devices.ExecuteUpdateAsync(set => set.SetProperty(d => d.CanSendToMe, false), Ct);
        TestCores.MakeReachable(sender, receiver);

        var jobId = await SendAsync(sender, receiver, [Source("a.txt", 10)]);

        Assert.Equal(HistoryOutcome.Declined, (await WaitForHistoryAsync(sender, jobId)).Outcome);
        Assert.False(Directory.Exists(TargetOf(receiver)));
    }

    [Theory]
    [InlineData(ExistingFilePolicy.KeepBoth)]
    [InlineData(ExistingFilePolicy.Replace)]
    [InlineData(ExistingFilePolicy.Skip)]
    public async Task Existing_files_follow_the_policy(ExistingFilePolicy policy)
    {
        var (sender, receiver) = await StartPairAsync();
        var file = Source("notes.txt", 64);
        var target = TargetOf(receiver);
        Directory.CreateDirectory(target);
        var existing = Path.Combine(target, "notes.txt");
        await File.WriteAllTextAsync(existing, "old", Ct);
        TestCores.MakeReachable(sender, receiver);

        var jobId = await SendAsync(sender, receiver, [file], policy);
        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(receiver, jobId)).Outcome);
        await WaitForHistoryAsync(sender, jobId);

        var content = await File.ReadAllBytesAsync(file, Ct);
        var item = Assert.Single(await ItemsOfAsync(receiver, jobId));
        switch (policy)
        {
            case ExistingFilePolicy.KeepBoth:
                Assert.Equal("old", await File.ReadAllTextAsync(existing, Ct));
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(target, "notes (2).txt"), Ct));
                Assert.Equal(Path.Combine(target, "notes (2).txt"), item.ResultPath);
                break;
            case ExistingFilePolicy.Replace:
                Assert.Equal(content, await File.ReadAllBytesAsync(existing, Ct));
                Assert.Single(Directory.GetFiles(target));
                break;
            case ExistingFilePolicy.Skip:
                Assert.Equal("old", await File.ReadAllTextAsync(existing, Ct));
                Assert.Equal(JobItemState.Skipped, item.State);
                Assert.Equal(JobItemState.Skipped, Assert.Single(await ItemsOfAsync(sender, jobId)).State);
                break;
        }
    }

    [Fact]
    public async Task Job_waits_while_the_device_is_offline_and_sends_the_changed_file_later()
    {
        var (sender, receiver) = await StartPairAsync();
        var file = Source("draft.txt", 1000);
        var target = TargetOf(receiver);

        var jobId = await SendAsync(sender, receiver, [file]);
        await Task.Delay(1500, Ct);
        Assert.Equal(JobState.Waiting, (await WaitForJobAsync(sender, jobId, _ => true)).State);

        await File.WriteAllTextAsync(file, "edited after the job was created", Ct);
        TestCores.MakeReachable(sender, receiver);

        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(sender, jobId)).Outcome);
        await WaitForHistoryAsync(receiver, jobId);
        Assert.Equal("edited after the job was created", await File.ReadAllTextAsync(Path.Combine(target, "draft.txt"), Ct));
    }

    [Fact]
    public async Task Receiver_restart_mid_job_resumes_from_the_journal()
    {
        var sender = await StartAsync("laptop");
        sender.Settings.Update(s => s with { UploadLimitBytesPerSecond = 16 * 1024 * 1024 });
        var receiverData = DataFor("workstation");
        var receiver = await TestCores.StartAsync(receiverData, Ct);
        await TestCores.PairAsync(sender, receiver, Ct);
        await sender.Presence.ReloadPairedDevicesAsync(Ct);
        var target = TargetOf(receiver);
        var file = Source("video.bin", 24 * ProtocolLimits.ChunkSize);
        TestCores.MakeReachable(sender, receiver);

        var jobId = await SendAsync(sender, receiver, [file]);
        await WaitForJobAsync(receiver, jobId, j => j.CurrentChunk >= 4);
        await receiver.DisposeAsync(); // "app closed" in the middle of the file

        var waiting = await WaitForJobAsync(sender, jobId, j => j.State == JobState.Waiting);
        Assert.NotNull(waiting.LastError);

        var restarted = await TestCores.StartAsync(receiverData, Ct);
        _cores.Add(restarted);
        TestCores.MakeReachable(sender, restarted);
        var resumed = await WaitForJobAsync(restarted, jobId, j => j.CurrentChunkCount > 0);

        Assert.True(resumed.ResumedChunks >= 4, $"resumed with {resumed.ResumedChunks} chunks");
        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(restarted, jobId)).Outcome);
        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(sender, jobId)).Outcome);
        Assert.Equal(await File.ReadAllBytesAsync(file, Ct), await File.ReadAllBytesAsync(Path.Combine(target, "video.bin"), Ct));
    }

    [Fact]
    public async Task Pause_and_resume_on_the_sender()
    {
        var (sender, receiver) = await StartPairAsync();
        sender.Settings.Update(s => s with { UploadLimitBytesPerSecond = 16 * 1024 * 1024 });
        var target = TargetOf(receiver);
        var file = Source("big.bin", 16 * ProtocolLimits.ChunkSize);
        TestCores.MakeReachable(sender, receiver);
        TestCores.MakeReachable(receiver, sender);

        var jobId = await SendAsync(sender, receiver, [file]);
        await WaitForJobAsync(sender, jobId, j => j.CurrentChunk >= 2);
        await sender.Transfers.PauseAsync(jobId);

        Assert.Equal(JobState.Paused, (await WaitForJobAsync(sender, jobId, j => j.State == JobState.Paused)).State);
        var onReceiver = await WaitForJobAsync(receiver, jobId, j => j.State == JobState.Paused);
        Assert.True(onReceiver.PausedByPeer);
        sender.Settings.Update(s => s with { UploadLimitBytesPerSecond = 0 });

        await sender.Transfers.ResumeAsync(jobId);

        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(receiver, jobId)).Outcome);
        Assert.Equal(await File.ReadAllBytesAsync(file, Ct), await File.ReadAllBytesAsync(Path.Combine(target, "big.bin"), Ct));
    }

    [Fact]
    public async Task Cancel_on_the_receiver_ends_the_job_on_both_sides_and_removes_temporary_files()
    {
        var (sender, receiver) = await StartPairAsync();
        sender.Settings.Update(s => s with { UploadLimitBytesPerSecond = 8 * 1024 * 1024 });
        var target = TargetOf(receiver);
        TestCores.MakeReachable(sender, receiver);

        var jobId = await SendAsync(sender, receiver, [Source("big.bin", 16 * ProtocolLimits.ChunkSize)]);
        await WaitForJobAsync(receiver, jobId, j => j.CurrentChunk >= 2);
        await receiver.Transfers.CancelAsync(jobId);

        Assert.Equal(HistoryOutcome.Canceled, (await WaitForHistoryAsync(receiver, jobId)).Outcome);
        Assert.Equal(HistoryOutcome.Canceled, (await WaitForHistoryAsync(sender, jobId)).Outcome);
        Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Full_target_pauses_the_job_until_the_receiver_resumes()
    {
        long free = 0;
        var receiverOptions = TestCores.ForTests(DataFor("workstation")) with
        {
            Receiver = new ReceiverOptions { AvailableFreeSpace = _ => Interlocked.Read(ref free) },
        };
        var (sender, receiver) = await StartPairAsync(receiverOptions);
        var target = TargetOf(receiver);
        var file = Source("big.bin", 2 * ProtocolLimits.ChunkSize);
        TestCores.MakeReachable(sender, receiver);
        TestCores.MakeReachable(receiver, sender);

        var jobId = await SendAsync(sender, receiver, [file]);

        var paused = await WaitForJobAsync(receiver, jobId, j => j.State == JobState.Paused);
        Assert.False(paused.PausedByPeer);
        Assert.Contains("free", paused.LastError);
        var onSender = await WaitForJobAsync(sender, jobId, j => j.State == JobState.Paused);
        Assert.True(onSender.PausedByPeer);

        Interlocked.Exchange(ref free, long.MaxValue);
        await receiver.Transfers.ResumeAsync(jobId);

        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(sender, jobId)).Outcome);
        Assert.Equal(await File.ReadAllBytesAsync(file, Ct), await File.ReadAllBytesAsync(Path.Combine(target, "big.bin"), Ct));
    }
}
