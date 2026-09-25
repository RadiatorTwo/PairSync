using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Connections;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.IntegrationTests;

/// <summary>Two (or three) application cores on loopback: mutual TLS with device keys and the Hello authorization.</summary>
public sealed class ConnectionTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-connect-");
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

    [Fact]
    public async Task Paired_devices_connect_and_know_who_is_on_the_other_side()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        await TestCores.PairAsync(a, b, Ct);
        var incoming = TestCores.NextIncomingAsync(b);

        await using var outgoing = await TestCores.ConnectAsync(a, b, Ct);
        await using var answer = await incoming;

        Assert.Equal(PeerAccess.Paired, outgoing.Access);
        Assert.Equal(b.Identity.Identity.Id, outgoing.Device!.Id);
        Assert.Equal(PeerAccess.Paired, answer.Access);
        Assert.Equal(a.Identity.Identity.Id, answer.Device!.Id);
        Assert.Equal(a.Identity.Identity.PublicKey, answer.RemotePublicKey);
        Assert.Equal(b.Identity.Identity.PublicKey, outgoing.RemotePublicKey);
        Assert.Equal("laptop", answer.Handshake.RemoteName);
        Assert.Equal(ProtocolVersion.Minor, answer.Handshake.RemoteMinor);
        Assert.True(b.Lan.Port > 0);
    }

    [Fact]
    public async Task Unknown_device_only_gets_a_pairing_session_and_cannot_start_a_transfer()
    {
        var stranger = await StartAsync("stranger");
        var b = await StartAsync("workstation");
        var incoming = TestCores.NextIncomingAsync(b);

        await using var outgoing = await stranger.Lan.ConnectForPairingAsync([IPAddress.Loopback], b.Lan.Port, Ct);
        await using var answer = await incoming;
        Assert.Equal(PeerAccess.PairingOnly, outgoing.Access);
        Assert.Equal(PeerAccess.PairingOnly, answer.Access);
        Assert.Null(answer.Device);
        Assert.Equal(stranger.Identity.Identity.PublicKey, answer.RemotePublicKey);

        await outgoing.Channels.Control.SendAsync(new TransferPlan { TransferId = Guid.NewGuid(), FileName = "x", ChunkSize = 1, ChunkCount = 1 }, Ct);

        await Assert.ThrowsAsync<PeerNotAuthorizedException>(() => answer.Channels.Control.ExpectAsync<TransferPlan>(Ct));
        var refused = await Assert.ThrowsAsync<TransferCanceledException>(() => outgoing.Channels.Control.ExpectAsync<TransferPlanAck>(Ct));
        Assert.Equal(ControlChannel.NotPairedReason, refused.Reason);
    }

    [Fact]
    public async Task Data_from_an_unpaired_device_ends_the_session()
    {
        var stranger = await StartAsync("stranger");
        var b = await StartAsync("workstation");
        var incoming = TestCores.NextIncomingAsync(b);

        await using var outgoing = await stranger.Lan.ConnectForPairingAsync([IPAddress.Loopback], b.Lan.Port, Ct);
        await using var answer = await incoming;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        answer.Session.StateChanged += (_, _) => closed.TrySetResult();

        await outgoing.Channels.Data.SendAsync(new byte[1024], Ct);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.NotEqual(TransportState.Connected, answer.Session.State);
    }

    [Fact]
    public async Task Connecting_as_paired_fails_if_the_other_side_does_not_know_this_device()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        await TestCores.AddDeviceAsync(a, TestCores.DeviceEntryFor(b), Ct); // b removed a
        var incoming = TestCores.NextIncomingAsync(b);

        var error = await Assert.ThrowsAsync<PeerRejectedException>(() => TestCores.ConnectAsync(a, b, Ct));
        Assert.True(error.ByOtherDevice);
        await using var answer = await incoming;
        Assert.Equal(PeerAccess.PairingOnly, answer.Access);
    }

    [Fact]
    public async Task Blocked_device_is_refused()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        await TestCores.AddDeviceAsync(a, TestCores.DeviceEntryFor(b), Ct);
        var blocked = TestCores.DeviceEntryFor(a);
        blocked.Trust = DeviceTrust.Blocked;
        await TestCores.AddDeviceAsync(b, blocked, Ct);
        var incoming = TestCores.NextIncomingAsync(b);

        var error = await Assert.ThrowsAsync<PeerRejectedException>(() => TestCores.ConnectAsync(a, b, Ct));
        Assert.True(error.ByOtherDevice);
        Assert.Equal("this device is blocked", error.Reason);
        await Task.Delay(200, Ct);
        Assert.False(incoming.IsCompleted, "a refused device must not reach the connection handler");
    }

    [Fact]
    public async Task Device_blocked_here_is_not_connected_to()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var blocked = TestCores.DeviceEntryFor(b);
        blocked.Trust = DeviceTrust.Blocked;
        await TestCores.AddDeviceAsync(a, blocked, Ct);
        await TestCores.AddDeviceAsync(b, TestCores.DeviceEntryFor(a), Ct);

        var error = await Assert.ThrowsAsync<PeerRejectedException>(() => TestCores.ConnectAsync(a, b, blocked, Ct));
        Assert.False(error.ByOtherDevice);
    }

    [Fact]
    public async Task Device_id_must_belong_to_the_key()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        await TestCores.AddDeviceAsync(a, TestCores.DeviceEntryFor(b), Ct);
        var wrongId = TestCores.DeviceEntryFor(a);
        wrongId.Id = Guid.NewGuid();
        await TestCores.AddDeviceAsync(b, wrongId, Ct);

        var error = await Assert.ThrowsAsync<PeerRejectedException>(() => TestCores.ConnectAsync(a, b, Ct));
        Assert.Contains("does not match its key", error.Reason);
    }

    [Fact]
    public async Task Another_device_on_the_expected_address_is_not_accepted()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var impostor = await StartAsync("impostor");
        await TestCores.PairAsync(a, b, Ct);
        await TestCores.AddDeviceAsync(impostor, TestCores.DeviceEntryFor(a), Ct);

        // a expects b's key, but the impostor answers on the port.
        await Assert.ThrowsAsync<TransportException>(() =>
            a.Lan.ConnectAsync(TestCores.DeviceEntryFor(b), [IPAddress.Loopback], impostor.Lan.Port, Ct));
    }

    [Fact]
    public async Task Busy_port_is_reported_instead_of_failing_the_start()
    {
        var a = await StartAsync("laptop");
        await using var second = await PairSyncCore.StartAsync(
            new DataDirectory(Path.Combine(_root.FullName, "second")), Ct,
            services => services.AddSingleton(new LanOptions { Port = a.Lan.Port }));

        Assert.Equal(0, second.Lan.Port);
        Assert.Contains(a.Lan.Port.ToString(), second.Lan.ListenError);
    }
}
