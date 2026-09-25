using System.Security.Cryptography;
using PairSync.Application;
using PairSync.Application.Presence;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.Storage;

namespace PairSync.IntegrationTests;

/// <summary>Permissions after pairing (plan §11): may send to me, blocked, removed.</summary>
public sealed class DeviceTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-devices-");
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

    private async Task<PairSyncCore> StartAsync(string name)
    {
        var core = await TestCores.StartAsync(new DataDirectory(Path.Combine(_root.FullName, name)), Ct);
        core.Settings.Update(s => s with { DeviceName = name });
        _cores.Add(core);
        return core;
    }

    private async Task<(PairSyncCore A, PairSyncCore B)> StartPairAsync()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        await TestCores.PairAsync(a, b, Ct);
        await a.Presence.ReloadPairedDevicesAsync(Ct);
        await b.Presence.ReloadPairedDevicesAsync(Ct);
        return (a, b);
    }

    /// <summary>A job to <paramref name="to"/> that waits, because <paramref name="to"/> is not reachable.</summary>
    private async Task<Guid> WaitingJobAsync(PairSyncCore from, PairSyncCore to)
    {
        var path = Path.Combine(_root.FullName, "source", Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(1000));
        return await from.Transfers.SendAsync(to.Identity.Identity.Id, SendScanner.Scan([path], Ct), ExistingFilePolicy.KeepBoth, null, Ct);
    }

    [Fact]
    public async Task Paired_devices_are_listed_by_name()
    {
        var (a, b) = await StartPairAsync();
        var c = await StartAsync("atelier");
        await TestCores.AddDeviceAsync(a, TestCores.DeviceEntryFor(c), Ct);

        var devices = await a.Devices.GetPairedAsync(Ct);

        Assert.Equal(["atelier", "workstation"], devices.Select(d => d.Name));
        Assert.All(devices, d => Assert.Equal(DeviceTrust.Active, d.Trust));
        Assert.Equal(b.Identity.Identity.Id, devices[1].Id);
    }

    [Fact]
    public async Task May_send_to_me_is_stored()
    {
        var (a, b) = await StartPairAsync();
        var changes = 0;
        a.Devices.Changed += () => changes++;

        await a.Devices.SetCanSendToMeAsync(b.Identity.Identity.Id, false, Ct);

        Assert.False((await a.Devices.GetPairedAsync(Ct)).Single().CanSendToMe);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Blocking_pauses_jobs_and_shows_in_presence()
    {
        var (a, b) = await StartPairAsync();
        var jobId = await WaitingJobAsync(a, b);
        Assert.EndsWith(".bin", (await a.Transfers.GetJobsAsync(Ct)).Single().Title, StringComparison.Ordinal);

        await a.Devices.SetBlockedAsync(b.Identity.Identity.Id, true, Ct);

        Assert.Equal(DeviceTrust.Blocked, (await a.Devices.GetPairedAsync(Ct)).Single().Trust);
        Assert.Equal(JobState.Paused, (await a.Transfers.GetJobsAsync(Ct)).Single(j => j.Id == jobId).State);
        Assert.Equal(DeviceTrust.Blocked, a.Presence.Devices.Single().Trust);

        await a.Devices.SetBlockedAsync(b.Identity.Identity.Id, false, Ct);
        Assert.Equal(DeviceTrust.Active, (await a.Devices.GetPairedAsync(Ct)).Single().Trust);
    }

    [Fact]
    public async Task Blocked_device_cannot_connect_for_transfers()
    {
        var (a, b) = await StartPairAsync();
        await a.Devices.SetBlockedAsync(b.Identity.Identity.Id, true, Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => TestCores.ConnectAsync(b, a, Ct));
    }

    [Fact]
    public async Task Removing_cancels_jobs_and_forgets_the_device()
    {
        var (a, b) = await StartPairAsync();
        var jobId = await WaitingJobAsync(a, b);

        await a.Devices.RemoveAsync(b.Identity.Identity.Id, Ct);

        Assert.Empty(await a.Devices.GetPairedAsync(Ct));
        Assert.DoesNotContain(await a.Transfers.GetJobsAsync(Ct), j => j.Id == jobId);
        var history = (await a.Transfers.GetHistoryAsync(10, Ct)).Single(h => h.JobId == jobId);
        Assert.Equal(HistoryOutcome.Canceled, history.Outcome);
        Assert.EndsWith(".bin", history.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(a.Presence.Devices, d => d.Id == b.Identity.Identity.Id && d.State != PresenceState.Found);
        await Assert.ThrowsAsync<ArgumentException>(() => a.Devices.SetBlockedAsync(b.Identity.Identity.Id, true, Ct));
    }
}
