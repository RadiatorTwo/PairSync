using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Presence;
using PairSync.Discovery.Lan;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;

namespace PairSync.IntegrationTests;

/// <summary>Two cores with real mDNS over loopback: discovery, presence states and connecting to a discovered device.</summary>
public sealed class PresenceTests : IAsyncLifetime
{
    private static readonly PresenceOptions FastLoopback = new()
    {
        QueryInterval = TimeSpan.FromSeconds(1),
        Expiry = TimeSpan.FromSeconds(4),
        IncludeLoopback = true,
    };

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-presence-");
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
        var core = await TestCores.StartAsync(new DataDirectory(Path.Combine(_root.FullName, name)), Ct, FastLoopback);
        core.Settings.Update(s => s with { DeviceName = name });
        _cores.Add(core);
        return core;
    }

    private static async Task<NearbyDevice> WaitForAsync(PairSyncCore core, Guid id, PresenceState state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (core.Presence.Devices.FirstOrDefault(d => d.Id == id && d.State == state) is { } device)
                return device;
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException($"{id} did not become {state}; saw: {string.Join(", ", core.Presence.Devices)}");
    }

    [Fact]
    public async Task Paired_device_comes_online_is_connectable_and_goes_offline()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        await TestCores.AddDeviceAsync(a, TestCores.DeviceEntryFor(b), Ct);
        await TestCores.AddDeviceAsync(b, TestCores.DeviceEntryFor(a), Ct);
        var available = new TaskCompletionSource<(PairedDevice Device, LanServiceInfo Info)>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Presence.PairedDeviceAvailable += (device, info) => available.TrySetResult((device, info));
        await a.Presence.ReloadPairedDevicesAsync(Ct);
        await b.Presence.ReloadPairedDevicesAsync(Ct);

        var bId = b.Identity.Identity.Id;
        var online = await WaitForAsync(a, bId, PresenceState.Online);
        Assert.Equal("workstation", online.Name);
        Assert.Equal(b.Lan.Port, online.Lan!.Port);
        Assert.Equal(ProtocolVersion.Current, online.Lan.ProtocolVersion);

        // The announced endpoint is enough to connect; the key check comes from the device list.
        var (device, info) = await available.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(bId, device.Id);
        var incoming = TestCores.NextIncomingAsync(b);
        await using (var connection = await a.Lan.ConnectAsync(device, info.Addresses, info.Port, Ct))
        await using (await incoming)
            Assert.Equal(PeerAccess.Paired, connection.Access);

        _cores.Remove(b);
        await b.DisposeAsync(); // says goodbye
        var offline = await WaitForAsync(a, bId, PresenceState.Offline);
        Assert.NotNull(offline.LastSeenUtc);
        Assert.Null(a.Presence.FindEndpoint(bId));

        await using var db = await a.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((await db.Devices.AsNoTracking().SingleAsync(d => d.Id == bId, Ct)).LastSeenUtc is null && DateTime.UtcNow < deadline)
            await Task.Delay(100, Ct);
        Assert.NotNull((await db.Devices.AsNoTracking().SingleAsync(d => d.Id == bId, Ct)).LastSeenUtc);
    }

    [Fact]
    public async Task Unpaired_device_is_listed_as_found_under_a_short_name()
    {
        var a = await StartAsync("laptop");
        var stranger = await StartAsync("stranger");
        var strangerId = stranger.Identity.Identity.Id;

        var found = await WaitForAsync(a, strangerId, PresenceState.Found);

        Assert.False(found.IsPaired);
        Assert.Equal(NearbyDevice.FoundName(strangerId), found.Name);
        Assert.Matches("^device-[0-9A-F]{4}$", found.Name);
        Assert.DoesNotContain(a.Presence.Devices, d => d.Id == a.Identity.Identity.Id);
    }

    [Fact]
    public async Task Pairing_while_online_raises_available_and_blocked_devices_do_not()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var c = await StartAsync("tablet");
        var available = new List<Guid>();
        a.Presence.PairedDeviceAvailable += (device, _) => { lock (available) available.Add(device.Id); };
        await WaitForAsync(a, b.Identity.Identity.Id, PresenceState.Found);
        await WaitForAsync(a, c.Identity.Identity.Id, PresenceState.Found);

        await TestCores.AddDeviceAsync(a, TestCores.DeviceEntryFor(b), Ct);
        var blocked = TestCores.DeviceEntryFor(c);
        blocked.Trust = DeviceTrust.Blocked;
        await TestCores.AddDeviceAsync(a, blocked, Ct);
        await a.Presence.ReloadPairedDevicesAsync(Ct);

        lock (available)
            Assert.Equal([b.Identity.Identity.Id], available);
        var blockedCard = a.Presence.Devices.Single(d => d.Id == c.Identity.Identity.Id);
        Assert.Equal(PresenceState.Online, blockedCard.State);
        Assert.Equal(DeviceTrust.Blocked, blockedCard.Trust);
    }
}
