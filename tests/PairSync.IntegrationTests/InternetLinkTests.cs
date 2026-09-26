using System.Security.Cryptography;
using PairSync.Application;
using PairSync.Application.Connections;
using PairSync.Application.Internet;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.Storage;
using PairSync.Transport;

namespace PairSync.IntegrationTests;

/// <summary>
/// Internet links between two cores in one process (phase 2 block C): WebRTC over host candidates, codes handed
/// over in memory. Presence is off, so the LAN path is not available unless a test adds it.
/// </summary>
public sealed class InternetLinkTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-internet-");
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

    private static InternetOptions Fast => new()
    {
        DetectNat = false,
        PingInterval = TimeSpan.FromMilliseconds(500),
        DisconnectGracePeriod = TimeSpan.FromSeconds(2),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    private async Task<PairSyncCore> StartAsync(string name, InternetOptions? internet = null)
    {
        var data = new DataDirectory(Path.Combine(_root.FullName, name));
        var core = await TestCores.StartAsync(data, Ct, internet: internet ?? Fast);
        core.Settings.Update(s => s with { DeviceName = name });
        _cores.Add(core);
        return core;
    }

    private async Task<(PairSyncCore A, PairSyncCore B)> StartPairAsync(InternetOptions? a = null, InternetOptions? b = null)
    {
        var laptop = await StartAsync("laptop", a);
        var office = await StartAsync("office-pc", b);
        await TestCores.PairAsync(laptop, office, Ct);
        return (laptop, office);
    }

    private static Guid Id(PairSyncCore core) => core.Identity.Identity.Id;

    /// <summary>A creates a code, B answers, A applies the answer; both sides end up with a link.</summary>
    private static async Task ConnectAsync(PairSyncCore a, PairSyncCore b)
    {
        var code = await a.Internet.CreateCodeAsync(Id(b), Ct);
        var (device, answer) = await b.Internet.AnswerCodeAsync(code.Text, Ct);
        Assert.Equal(Id(a), device.Id);
        await a.Internet.ApplyAnswerAsync(answer.Text, Ct);
        await WaitAsync(() => b.Internet.LinkTo(Id(a)) is not null, "B has no link");
    }

    private static async Task WaitAsync(Func<bool> condition, string message, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(message);
            await Task.Delay(50, Ct);
        }
    }

    private string Source(string relative, int size)
    {
        var path = Path.Combine(_root.FullName, "source", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size));
        return path;
    }

    private static string TargetOf(PairSyncCore receiver, string sender) =>
        Path.Combine(receiver.DataDirectory.Root, "downloads", "PairSync", sender);

    private static Task<Guid> SendAsync(PairSyncCore from, PairSyncCore to, params string[] paths) =>
        from.Transfers.SendAsync(Id(to), SendScanner.Scan(paths, Ct), ExistingFilePolicy.KeepBoth, suggestedFolder: null, Ct);

    private static async Task<HistoryEntry> WaitForHistoryAsync(PairSyncCore core, Guid jobId, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if ((await core.Transfers.GetHistoryAsync(50, Ct)).FirstOrDefault(h => h.JobId == jobId) is { } entry)
                return entry;
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException($"Job {jobId} did not finish; jobs: {string.Join("; ", await core.Transfers.GetJobsAsync(Ct))}");
    }

    [Fact]
    public async Task Paired_devices_connect_by_exchanging_codes()
    {
        var (a, b) = await StartPairAsync();

        var code = await a.Internet.CreateCodeAsync(Id(b), Ct);
        Assert.StartsWith("PSC1:", code.Text);
        Assert.Equal(InternetLinkPhase.WaitingForAnswer, a.Internet.StatusOf(Id(b))!.Phase);
        Assert.Equal(PeerRoute.None, a.Links.RouteTo(Id(b)));

        var (_, answer) = await b.Internet.AnswerCodeAsync(code.Text, Ct);
        Assert.StartsWith("PSR1:", answer.Text);
        Assert.Equal(InternetLinkPhase.WaitingForConnection, b.Internet.StatusOf(Id(a))!.Phase);

        var device = await a.Internet.ApplyAnswerAsync(answer.Text, Ct);
        Assert.Equal(Id(b), device.Id);
        await WaitAsync(() => b.Internet.StatusOf(Id(a))?.Phase == InternetLinkPhase.Connected, "B not connected");

        var status = a.Internet.StatusOf(Id(b))!;
        Assert.Equal(InternetLinkPhase.Connected, status.Phase);
        Assert.NotNull(status.Route);
        Assert.Equal(PeerRoute.Internet, a.Links.RouteTo(Id(b)));
        Assert.Equal(PeerRoute.Internet, b.Links.RouteTo(Id(a)));
        await WaitAsync(() => a.Internet.StatusOf(Id(b))?.RoundTrip is not null, "no round trip measured");
    }

    [Fact]
    public async Task Jobs_run_in_both_directions_over_one_link_without_new_codes()
    {
        var (a, b) = await StartPairAsync();
        await ConnectAsync(a, b);
        var folder = Path.GetDirectoryName(Source("Project/data/big.bin", 2 * PairSync.Protocol.ProtocolLimits.ChunkSize + 777))!;
        Source("Project/readme.md", 42);
        var back = Source("reply.txt", 1234);

        var toB = await SendAsync(a, b, Path.GetDirectoryName(folder)!);
        var toA = await SendAsync(b, a, back);
        var second = await SendAsync(a, b, Source("second.txt", 99));

        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(b, toB)).Outcome);
        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(a, toA)).Outcome);
        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(b, second)).Outcome);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(folder, "big.bin"), Ct),
            await File.ReadAllBytesAsync(Path.Combine(TargetOf(b, "laptop"), "Project", "data", "big.bin"), Ct));
        Assert.Equal(await File.ReadAllBytesAsync(back, Ct), await File.ReadAllBytesAsync(Path.Combine(TargetOf(a, "office-pc"), "reply.txt"), Ct));
        Assert.Equal(InternetLinkPhase.Connected, a.Internet.StatusOf(Id(b))!.Phase);
    }

    [Fact]
    public async Task Lan_is_preferred_when_the_device_is_on_the_lan()
    {
        var (a, b) = await StartPairAsync();
        await ConnectAsync(a, b);

        TestCores.MakeReachable(a, b);

        Assert.Equal(PeerRoute.Lan, a.Links.RouteTo(Id(b)));
        var job = await SendAsync(a, b, Source("x.txt", 10));
        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(b, job)).Outcome);
    }

    [Fact]
    public async Task Lost_link_leaves_the_job_waiting_and_new_codes_resume_it()
    {
        var (a, b) = await StartPairAsync();
        await ConnectAsync(a, b);
        a.Settings.Update(s => s with { UploadLimitBytesPerSecond = 2 * 1024 * 1024 });
        var file = Source("big.bin", 6 * PairSync.Protocol.ProtocolLimits.ChunkSize);

        var job = await SendAsync(a, b, file);
        await WaitAsync(() => a.Transfers.GetJobsAsync(Ct).Result.FirstOrDefault(j => j.Id == job) is { State: JobState.Running, TransferredBytes: > 0 },
            "job did not start");

        // B's connection drops without a Goodbye: A notices the loss, the job waits without an error loop.
        await b.Internet.LinkTo(Id(a))!.Session.DisposeAsync();
        await WaitAsync(() => a.Internet.StatusOf(Id(b))?.Phase == InternetLinkPhase.Failed, "A did not notice the loss");
        Assert.True(a.Internet.StatusOf(Id(b))!.WasConnected);
        Assert.Contains("lost", a.Internet.StatusOf(Id(b))!.Error);
        await WaitAsync(() => a.Transfers.GetJobsAsync(Ct).Result.Single(j => j.Id == job).State == JobState.Waiting, "job not waiting");

        a.Settings.Update(s => s with { UploadLimitBytesPerSecond = 0 });
        await ConnectAsync(a, b);

        Assert.Equal(HistoryOutcome.Completed, (await WaitForHistoryAsync(b, job)).Outcome);
        Assert.Equal(await File.ReadAllBytesAsync(file, Ct), await File.ReadAllBytesAsync(Path.Combine(TargetOf(b, "laptop"), "big.bin"), Ct));
    }

    [Fact]
    public async Task Closing_on_purpose_leaves_the_other_side_offline_without_a_lost_connection()
    {
        var (a, b) = await StartPairAsync();
        await ConnectAsync(a, b);

        // B quits: it says Goodbye before the connection goes away.
        _cores.Remove(b);
        await b.DisposeAsync();

        await WaitAsync(() => a.Internet.StatusOf(Id(b)) is null, "A still shows an internet status", seconds: 5);
        Assert.Null(a.Internet.LinkTo(Id(b)));
    }

    [Fact]
    public async Task Each_code_works_once()
    {
        var (a, b) = await StartPairAsync();
        var code = await a.Internet.CreateCodeAsync(Id(b), Ct);
        var (_, answer) = await b.Internet.AnswerCodeAsync(code.Text, Ct);

        var again = await Assert.ThrowsAsync<InvalidConnectCodeException>(() => b.Internet.AnswerCodeAsync(code.Text, Ct));
        Assert.Contains("already answered", again.Message);

        await a.Internet.ApplyAnswerAsync(answer.Text, Ct);
        await Assert.ThrowsAsync<InvalidConnectCodeException>(() => a.Internet.ApplyAnswerAsync(answer.Text, Ct));
    }

    [Fact]
    public async Task Code_for_another_device_and_answers_from_the_wrong_device_are_refused()
    {
        var (a, b) = await StartPairAsync();
        var c = await StartAsync("stranger");
        await TestCores.PairAsync(a, c, Ct);

        var codeForB = await a.Internet.CreateCodeAsync(Id(b), Ct);
        var wrongTarget = await Assert.ThrowsAsync<InvalidConnectCodeException>(() => c.Internet.AnswerCodeAsync(codeForB.Text, Ct));
        Assert.Contains("another device", wrongTarget.Message);

        // C saw the code meant for B and answers it itself: the answer is signed, but by the wrong device.
        var codeForC = await a.Internet.CreateCodeAsync(Id(c), Ct);
        var (_, answerFromC) = await c.Internet.AnswerCodeAsync(codeForC.Text, Ct);
        var sdp = ConnectCodes.ReadAnswer(answerFromC.Text, DateTime.UtcNow, TimeSpan.FromMinutes(2), Id(a)).Answer;
        var forged = ConnectCodes.CreateAnswer(c.Identity.Identity, "office-pc", codeForB.Nonce, sdp, DateTime.UtcNow.AddMinutes(5),
            PairSync.Stun.NatHint.Unknown);

        var wrongDevice = await Assert.ThrowsAsync<InvalidConnectCodeException>(() => a.Internet.ApplyAnswerAsync(forged, Ct));
        Assert.Contains("does not come from office-pc", wrongDevice.Message);
        Assert.Equal(InternetLinkPhase.WaitingForAnswer, a.Internet.StatusOf(Id(b))!.Phase);
    }

    [Fact]
    public async Task Blocked_device_cannot_connect_and_blocking_closes_the_link()
    {
        var (a, b) = await StartPairAsync();
        await ConnectAsync(a, b);

        await b.Devices.SetBlockedAsync(Id(a), true, Ct);

        Assert.Null(b.Internet.LinkTo(Id(a)));
        await WaitAsync(() => a.Internet.LinkTo(Id(b)) is null, "A did not notice");
        var code = await a.Internet.CreateCodeAsync(Id(b), Ct);
        var refused = await Assert.ThrowsAsync<InvalidConnectCodeException>(() => b.Internet.AnswerCodeAsync(code.Text, Ct));
        Assert.Contains("blocked", refused.Message);
    }

    [Fact]
    public async Task Unused_code_expires()
    {
        var (a, b) = await StartPairAsync(a: Fast with { OfferLifetime = TimeSpan.FromSeconds(1) });
        var code = await a.Internet.CreateCodeAsync(Id(b), Ct);

        await WaitAsync(() => a.Internet.StatusOf(Id(b))?.Phase == InternetLinkPhase.Failed, "code did not expire");
        Assert.Contains("expired", a.Internet.StatusOf(Id(b))!.Error);
        a.Internet.Dismiss(Id(b));
        Assert.Null(a.Internet.StatusOf(Id(b)));
    }

    [Fact]
    public async Task Answering_side_gives_up_when_the_answer_is_never_applied()
    {
        var (a, b) = await StartPairAsync(b: Fast with { AnswerLifetime = TimeSpan.FromSeconds(2) });
        var code = await a.Internet.CreateCodeAsync(Id(b), Ct);
        await b.Internet.AnswerCodeAsync(code.Text, Ct);

        await WaitAsync(() => b.Internet.StatusOf(Id(a))?.Phase == InternetLinkPhase.Failed, "B did not give up");
        Assert.Contains("not applied in time", b.Internet.StatusOf(Id(a))!.Error);
    }
}
