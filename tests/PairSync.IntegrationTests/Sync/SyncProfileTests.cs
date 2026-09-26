using PairSync.Application;
using PairSync.Application.Sync;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;

namespace PairSync.IntegrationTests.Sync;

/// <summary>Two app cores keep folders in sync over the LAN (loopback) and over an internet link.</summary>
public sealed class SyncProfileTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-sync-");
    private readonly List<PairSyncCore> _cores = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var core in _cores)
            await core.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // a watcher handle may linger for a moment on Windows
        }
    }

    private async Task<PairSyncCore> StartAsync(string name, TransferOptions? transfers = null)
    {
        var data = new DataDirectory(Path.Combine(_root.FullName, name, "data"));
        var core = await TestCores.StartAsync(data, Ct, transfers: transfers);
        core.Settings.Update(s => s with { DeviceName = name });
        _cores.Add(core);
        return core;
    }

    private async Task<(PairSyncCore A, PairSyncCore B)> StartPairAsync(TransferOptions? a = null)
    {
        var first = await StartAsync("laptop", a);
        var second = await StartAsync("office");
        await TestCores.PairAsync(first, second, Ct);
        TestCores.MakeReachable(first, second);
        TestCores.MakeReachable(second, first);
        return (first, second);
    }

    private string Folder(string owner) => Directory.CreateDirectory(Path.Combine(_root.FullName, owner, "folder")).FullName;

    private static string Write(string folder, string path, string content) => WriteBytes(folder, path, System.Text.Encoding.UTF8.GetBytes(content));

    private static string WriteBytes(string folder, string path, byte[] content)
    {
        var full = Path.Combine(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        // Older than the scanner's settle time, so the next scan takes it.
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(-10));
        return full;
    }

    private static string? Read(string folder, string path) =>
        File.Exists(Path.Combine(folder, path)) ? File.ReadAllText(Path.Combine(folder, path)) : null;

    private static async Task WaitAsync(Func<bool> condition, string message, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(message);
            await Task.Delay(100, Ct);
        }
    }

    private static Guid Id(PairSyncCore core) => core.Identity.Identity.Id;

    /// <summary>A creates a profile for <paramref name="aFolder"/>, B accepts it for <paramref name="bFolder"/>.</summary>
    private static async Task<Guid> ShareAsync(
        PairSyncCore a, PairSyncCore b, string aFolder, string bFolder, SyncDirection direction = SyncDirection.TwoWay,
        SyncMode mode = SyncMode.Automatic, ProfileAcceptance? acceptance = null, string excludes = "")
    {
        var profile = await a.Sync.CreateProfileAsync(new NewSyncProfile("Projects", Id(b), aFolder, direction, mode, excludes), Ct);
        await WaitAsync(() => b.Sync.IncomingOffers.Any(o => o.ProfileId == profile.Id), "B got no offer");
        var offer = b.Sync.IncomingOffers.Single();
        Assert.Equal("Projects", offer.Name);
        Assert.Equal("laptop", offer.DeviceName);
        await b.Sync.AcceptOfferAsync(profile.Id, acceptance ?? new ProfileAcceptance(bFolder, direction.Mirror(), mode), Ct);
        await WaitAsync(() => a.Sync.GetProfilesAsync(Ct).Result.Single().Profile.State == SyncProfileState.Active, "A's profile did not become active");
        return profile.Id;
    }

    [Fact]
    public async Task Accepted_profile_syncs_changes_in_both_directions()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        Write(aFolder, "docs/plan.md", "from laptop");
        Directory.CreateDirectory(Path.Combine(aFolder, "empty"));

        await ShareAsync(a, b, aFolder, bFolder);

        await WaitAsync(() => Read(bFolder, "docs/plan.md") == "from laptop", "file did not reach B");
        await WaitAsync(() => Directory.Exists(Path.Combine(bFolder, "empty")), "empty folder did not reach B");

        Write(bFolder, "notes.txt", "from office");
        await WaitAsync(() => Read(aFolder, "notes.txt") == "from office", "file did not reach A");

        Write(aFolder, "docs/plan.md", "changed on laptop");
        await WaitAsync(() => Read(bFolder, "docs/plan.md") == "changed on laptop", "change did not reach B");
        Assert.True(Directory.Exists(Path.Combine(bFolder, ".pairsync", "trash")), "the replaced version is not in B's trash");
        Assert.Empty(await b.Sync.GetConflictsAsync((await b.Sync.GetProfilesAsync(Ct)).Single().Id, Ct));
    }

    [Fact]
    public async Task Declined_offer_leaves_the_profile_declined()
    {
        var (a, b) = await StartPairAsync();
        var profile = await a.Sync.CreateProfileAsync(new NewSyncProfile("Photos", Id(b), Folder("laptop"), SyncDirection.SendOnly, SyncMode.Automatic, ""), Ct);
        await WaitAsync(() => b.Sync.IncomingOffers.Count == 1, "B got no offer");
        Assert.Equal(SyncDirection.SendOnly, b.Sync.IncomingOffers.Single().Direction);

        await b.Sync.DeclineOfferAsync(profile.Id);

        await WaitAsync(() => a.Sync.GetProfilesAsync(Ct).Result.Single().Status == SyncStatus.Declined, "A did not hear about the decline");
        Assert.Empty(await b.Sync.GetProfilesAsync(Ct));
    }

    [Fact]
    public async Task First_sync_merges_non_empty_folders_and_keeps_both_versions_of_a_clash()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        Write(aFolder, "same.txt", "identical");
        Write(bFolder, "same.txt", "identical");
        Write(aFolder, "only-a.txt", "a");
        Write(bFolder, "only-b.txt", "b");
        Write(aFolder, "clash.txt", "laptop version");
        Write(bFolder, "clash.txt", "office version");

        await ShareAsync(a, b, aFolder, bFolder);

        await WaitAsync(() => Read(aFolder, "only-b.txt") == "b" && Read(bFolder, "only-a.txt") == "a", "one-sided files were not exchanged");
        await WaitAsync(() => Directory.GetFiles(aFolder, "clash (conflict *").Length == 1 && Directory.GetFiles(bFolder, "clash (conflict *").Length == 1,
            "conflict copies missing");
        await WaitAsync(() => Read(aFolder, "clash.txt") == Read(bFolder, "clash.txt"), "original paths differ");
        var copies = new[] { aFolder, bFolder }.Select(f => File.ReadAllText(Directory.GetFiles(f, "clash (conflict *").Single())).ToList();
        Assert.Equal(copies[0], copies[1]);
        Assert.Equal(["laptop version", "office version"], new[] { Read(aFolder, "clash.txt")!, copies[0] }.Order());
        Assert.Equal("identical", Read(aFolder, "same.txt"));
        Assert.Empty(Directory.GetFiles(aFolder, "same (conflict *"));

        var profileId = (await a.Sync.GetProfilesAsync(Ct)).Single().Id;
        await WaitAsync(() => a.Sync.GetConflictsAsync(profileId, Ct).Result.Count == 1 && b.Sync.GetConflictsAsync(profileId, Ct).Result.Count == 1,
            "conflict not recorded on both devices");
    }

    [Fact]
    public async Task Resolving_a_conflict_reaches_the_other_device()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        Write(aFolder, "clash.txt", "laptop version");
        Write(bFolder, "clash.txt", "office version");
        var profileId = await ShareAsync(a, b, aFolder, bFolder);
        await WaitAsync(() => a.Sync.GetConflictsAsync(profileId, Ct).Result.Count == 1 && b.Sync.GetConflictsAsync(profileId, Ct).Result.Count == 1
                              && Directory.GetFiles(aFolder, "clash (conflict *").Length == 1, "no conflict");

        var conflict = (await a.Sync.GetConflictsAsync(profileId, Ct)).Single();
        await a.Sync.ResolveConflictAsync(conflict.Id, ConflictResolution.KeepThisDevice, Ct);

        await WaitAsync(() => Read(aFolder, "clash.txt") == "laptop version" && Read(bFolder, "clash.txt") == "laptop version", "choice not applied on both");
        await WaitAsync(() => Directory.GetFiles(bFolder, "clash (conflict *").Length == 0 && Directory.GetFiles(aFolder, "clash (conflict *").Length == 0,
            "copies not removed");
        await WaitAsync(() => b.Sync.GetConflictsAsync(profileId, Ct).Result.Count == 0, "B still shows the conflict");
    }

    [Fact]
    public async Task Deletions_propagate_to_the_trash_and_do_not_come_back()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        var file = Write(aFolder, "old/report.txt", "x");
        await ShareAsync(a, b, aFolder, bFolder);
        await WaitAsync(() => Read(bFolder, "old/report.txt") == "x", "file did not reach B");

        File.Delete(file);
        Directory.Delete(Path.Combine(aFolder, "old"));

        await WaitAsync(() => !Directory.Exists(Path.Combine(bFolder, "old")), "deletion did not reach B");
        Assert.Single(Directory.GetFiles(Path.Combine(bFolder, ".pairsync", "trash"), "report.txt", SearchOption.AllDirectories));
        // Another round on both sides must not bring it back.
        var profileId = (await a.Sync.GetProfilesAsync(Ct)).Single().Id;
        await b.Sync.SyncNowAsync(profileId);
        await Task.Delay(1500, Ct);
        Assert.False(File.Exists(Path.Combine(aFolder, "old", "report.txt")));
    }

    [Fact]
    public async Task Changed_chunk_of_a_large_file_is_the_only_one_transferred()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        var data = new byte[ProtocolLimits.ChunkSize * 3 + 100];
        new Random(7).NextBytes(data);
        WriteBytes(aFolder, "big.bin", data);
        var profileId = await ShareAsync(a, b, aFolder, bFolder);
        await WaitAsync(() => File.Exists(Path.Combine(bFolder, "big.bin")), "file did not reach B", 60);

        data[ProtocolLimits.ChunkSize + 5] ^= 0xFF;
        WriteBytes(aFolder, "big.bin", data);

        await WaitAsync(() => File.ReadAllBytes(Path.Combine(bFolder, "big.bin")).AsSpan().SequenceEqual(data), "change did not reach B", 60);
        var view = (await b.Sync.GetProfilesAsync(Ct)).Single(p => p.Id == profileId);
        Assert.InRange(view.BytesDone, 1, ProtocolLimits.ChunkSize + 1024);
    }

    [Fact]
    public async Task Send_only_and_missing_delete_right_are_respected()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        var kept = Write(aFolder, "kept.txt", "x");
        await ShareAsync(a, b, aFolder, bFolder, SyncDirection.SendOnly,
            acceptance: new ProfileAcceptance(bFolder, SyncDirection.ReceiveOnly, SyncMode.Automatic, AllowDelete: false));
        await WaitAsync(() => Read(bFolder, "kept.txt") == "x", "file did not reach B");

        // B's own file does not go to A (A only sends), A's deletion does not reach B (no Delete right).
        Write(bFolder, "local-only.txt", "b");
        File.Delete(kept);
        Write(aFolder, "later.txt", "y");

        await WaitAsync(() => Read(bFolder, "later.txt") == "y", "later file did not reach B");
        await Task.Delay(1500, Ct);
        Assert.Equal("x", Read(bFolder, "kept.txt"));
        Assert.Null(Read(aFolder, "local-only.txt"));
    }

    [Fact]
    public async Task Manual_profile_syncs_only_on_sync_now()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        var profileId = await ShareAsync(a, b, aFolder, bFolder, mode: SyncMode.Manual);
        await Task.Delay(1000, Ct);

        Write(aFolder, "manual.txt", "x");
        await Task.Delay(2000, Ct);
        Assert.Null(Read(bFolder, "manual.txt"));

        await a.Sync.SyncNowAsync(profileId);

        await WaitAsync(() => Read(bFolder, "manual.txt") == "x", "Sync now did not sync");
    }

    [Fact]
    public async Task Paused_profile_does_not_take_changes()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        var profileId = await ShareAsync(a, b, aFolder, bFolder);
        await b.Sync.UpdateProfileAsync(profileId, p => p.Paused = true, Ct);

        Write(aFolder, "while-paused.txt", "x");
        await Task.Delay(2000, Ct);
        Assert.Null(Read(bFolder, "while-paused.txt"));

        await b.Sync.UpdateProfileAsync(profileId, p => p.Paused = false, Ct);
        await WaitAsync(() => Read(bFolder, "while-paused.txt") == "x", "resumed profile did not sync");
    }

    [Fact]
    public async Task Interrupted_fetch_resumes_without_resending_confirmed_chunks()
    {
        var (a, b) = await StartPairAsync(a: null);
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        var data = new byte[ProtocolLimits.ChunkSize * 4];
        new Random(3).NextBytes(data);
        WriteBytes(aFolder, "large.bin", data);
        a.Settings.Update(s => s with { UploadLimitBytesPerSecond = 3 * 1024 * 1024 });

        var profileId = await ShareAsync(a, b, aFolder, bFolder);
        await WaitAsync(() => Directory.Exists(Path.Combine(bFolder, ".pairsync", "tmp"))
                              && Directory.GetFiles(Path.Combine(bFolder, ".pairsync", "tmp")).Length > 0, "fetch did not start");
        await Task.Delay(1500, Ct);
        await b.Sync.UpdateProfileAsync(profileId, p => p.Paused = true, Ct); // stops the round's session
        a.Settings.Update(s => s with { UploadLimitBytesPerSecond = 0 });
        await b.Sync.UpdateProfileAsync(profileId, p => p.Paused = false, Ct);

        await WaitAsync(() => File.Exists(Path.Combine(bFolder, "large.bin")), "file did not arrive", 60);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(bFolder, "large.bin"), Ct));
        // The resumed run fetched only what the first one had not confirmed.
        var view = (await b.Sync.GetProfilesAsync(Ct)).Single(p => p.Id == profileId);
        Assert.InRange(view.BytesDone, 1, data.Length - ProtocolLimits.ChunkSize);
    }

    [Fact]
    public async Task Profile_syncs_over_an_internet_link()
    {
        var first = await StartAsync("laptop");
        var second = await StartAsync("office");
        await TestCores.PairAsync(first, second, Ct);
        var code = await first.Internet.CreateCodeAsync(Id(second), Ct);
        var (_, answer) = await second.Internet.AnswerCodeAsync(code.Text, Ct);
        await first.Internet.ApplyAnswerAsync(answer.Text, Ct);
        await WaitAsync(() => second.Internet.LinkTo(Id(first)) is not null, "no link");

        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        Write(aFolder, "over-internet.txt", "hello");
        await ShareAsync(first, second, aFolder, bFolder);

        await WaitAsync(() => Read(bFolder, "over-internet.txt") == "hello", "file did not arrive over the internet link", 60);
    }

    [Fact]
    public async Task Removed_profile_detaches_the_other_side_and_keeps_the_files()
    {
        var (a, b) = await StartPairAsync();
        var (aFolder, bFolder) = (Folder("laptop"), Folder("office"));
        Write(aFolder, "stay.txt", "x");
        var profileId = await ShareAsync(a, b, aFolder, bFolder);
        await WaitAsync(() => Read(bFolder, "stay.txt") == "x", "file did not reach B");

        await a.Sync.RemoveProfileAsync(profileId, Ct);

        await WaitAsync(() => b.Sync.GetProfilesAsync(Ct).Result.Single().Status == SyncStatus.Detached, "B's profile not detached");
        Assert.Equal("x", Read(aFolder, "stay.txt"));
        Assert.Equal("x", Read(bFolder, "stay.txt"));
    }
}
