using Microsoft.AspNetCore.Builder;
using PairSync.Application;
using PairSync.Application.Rendezvous;
using PairSync.Domain;
using PairSync.Rendezvous;
using PairSync.Storage;
using PairSync.Transport;

namespace PairSync.IntegrationTests;

/// <summary>Two cores logged in to a rendezvous service on loopback (phase 5 block C).</summary>
public sealed class RendezvousClientTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-rv-client-");
    private readonly List<PairSyncCore> _cores = [];
    private WebApplication _app = null!;
    private Uri _url = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => (_app, _url) = await RendezvousHost.StartLocalAsync(o =>
    {
        o.Turn.Uris = ["turn:relay.example.org:3478"];
        o.Turn.Secret = "secret";
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var core in _cores)
            await core.DisposeAsync();
        await _app.DisposeAsync();
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
        var b = await StartAsync("office-pc");
        await TestCores.PairAsync(a, b, Ct);
        await a.Presence.ReloadPairedDevicesAsync(Ct);
        await b.Presence.ReloadPairedDevicesAsync(Ct);
        return (a, b);
    }

    private void Enable(PairSyncCore core) => core.Settings.Update(s => s with { RendezvousUrl = _url.ToString() });

    private static string AddressOf(PairSyncCore core) => RendezvousClient.AddressOf(TestCores.DeviceEntryFor(core));

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            try
            {
                await Task.Delay(50, timeout.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }
        }
    }

    [Fact]
    public async Task Without_url_the_client_stays_off()
    {
        var core = await StartAsync("laptop");

        Assert.Equal(SignalingState.Off, core.Rendezvous.State);
        Assert.Empty(core.Relays.Provided);
    }

    [Fact]
    public async Task Client_logs_in_and_receives_relay_credentials()
    {
        var core = await StartAsync("laptop");

        Enable(core);

        await WaitUntilAsync(() => core.Rendezvous.State == SignalingState.Connected, "login");
        var relay = Assert.Single(core.Relays.Provided);
        Assert.Equal("turn:relay.example.org:3478", relay.Uri);
        Assert.Contains(core.Relays.IceServers(), s => s.StartsWith("turn:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paired_devices_see_each_other_online_and_exchange_messages()
    {
        var (a, b) = await StartPairAsync();
        var received = new TaskCompletionSource<(string From, string Data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Rendezvous.MessageReceived += (from, data) => received.TrySetResult((from, data));

        Enable(a);
        Enable(b);

        await WaitUntilAsync(() => a.Rendezvous.IsOnline(AddressOf(b)) && b.Rendezvous.IsOnline(AddressOf(a)), "presence");
        Assert.True(await a.Rendezvous.SendAsync(AddressOf(b), "hello", Ct));
        Assert.Equal((AddressOf(a), "hello"), await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Turning_the_service_off_ends_presence()
    {
        var (a, b) = await StartPairAsync();
        Enable(a);
        Enable(b);
        await WaitUntilAsync(() => a.Rendezvous.IsOnline(AddressOf(b)), "presence");

        b.Settings.Update(s => s with { RendezvousUrl = null });

        await WaitUntilAsync(() => !a.Rendezvous.IsOnline(AddressOf(b)), "offline");
        Assert.Equal(SignalingState.Off, b.Rendezvous.State);
    }

    [Fact]
    public async Task Blocked_device_is_not_watched()
    {
        var (a, b) = await StartPairAsync();
        Enable(a);
        Enable(b);
        await WaitUntilAsync(() => a.Rendezvous.IsOnline(AddressOf(b)), "presence");

        await a.Devices.SetBlockedAsync(b.Identity.Identity.Id, true, Ct);

        await WaitUntilAsync(() => !a.Rendezvous.IsOnline(AddressOf(b)), "unwatched");
    }

    [Fact]
    public async Task Unreachable_service_is_reported_and_retried()
    {
        var core = await StartAsync("laptop");

        core.Settings.Update(s => s with { RendezvousUrl = "ws://127.0.0.1:1/v1" });

        await WaitUntilAsync(() => core.Rendezvous.Status is { State: SignalingState.Failed, Error: not null }, "failure");
        Assert.NotNull(core.Rendezvous.Status.NextAttemptUtc);
    }
}
