using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Pairing;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;

namespace PairSync.IntegrationTests;

/// <summary>Two application cores pair over loopback: invitation and LAN path, confirmation, cancel, timeout.</summary>
public sealed class PairingTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-pairing-");
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

    private async Task<PairSyncCore> StartAsync(string name, PairingOptions? pairing = null)
    {
        var core = await TestCores.StartAsync(new DataDirectory(Path.Combine(_root.FullName, name)), Ct,
            pairing: pairing is null ? null : pairing with { InvitationAddresses = [IPAddress.Loopback] });
        core.Settings.Update(s => s with { DeviceName = name });
        _cores.Add(core);
        return core;
    }

    /// <summary>Completes with the next pairing request <paramref name="core"/> receives.</summary>
    private static Task<PairingSession> NextRequestAsync(PairSyncCore core)
    {
        var next = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Pairing.IncomingRequest += session => next.TrySetResult(session);
        return next.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
    }

    private static async Task<List<PairedDevice>> DevicesOfAsync(PairSyncCore core)
    {
        await using var db = await core.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync(Ct);
        return await db.Devices.AsNoTracking().ToListAsync(Ct);
    }

    private static Task<PairingSession> PairOverLanAsync(PairSyncCore from, PairSyncCore to) =>
        from.Pairing.PairAsync(to.Identity.Identity.Id, [IPAddress.Loopback], to.Lan.Port, Ct);

    private static async Task<(PairedDevice OnA, PairedDevice OnB)> ConfirmBothAsync(PairingSession a, PairingSession b)
    {
        await a.ConfirmAsync(Ct);
        await b.ConfirmAsync(Ct);
        return (await a.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct), await b.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Invitation_pairing_stores_both_keys_once_both_confirm()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var request = NextRequestAsync(a);

        var invitation = a.Pairing.CreateInvitation();
        var received = b.Pairing.ReadInvitation(invitation.Text);
        Assert.Equal(a.Identity.Identity.Fingerprint, received.Fingerprint);
        Assert.Equal("laptop", received.DeviceName);

        var onB = await b.Pairing.PairAsync(received, Ct);
        var onA = await request;

        Assert.Equal(onA.SecurityCode, onB.SecurityCode);
        Assert.True(onA.ViaInvitation);
        Assert.True(onA.IsIncoming);
        Assert.False(onB.IsIncoming);
        Assert.Equal("workstation", onA.RemoteName);
        Assert.Equal(b.Identity.Identity.Fingerprint, onA.RemoteFingerprint);
        Assert.Equal(a.Identity.Identity.Fingerprint, onB.RemoteFingerprint);

        await onA.ConfirmAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.False(onA.Completion.IsCompleted, "one confirmation is not enough");
        Assert.Empty(await DevicesOfAsync(a));

        var (deviceOnA, deviceOnB) = await ConfirmBothAsync(onA, onB);

        Assert.Equal(b.Identity.Identity.PublicKey, deviceOnA.PublicKey);
        Assert.Equal(a.Identity.Identity.Id, deviceOnB.Id);
        Assert.Equal("laptop", deviceOnB.Name);
        Assert.Equal(DeviceTrust.Active, deviceOnB.Trust);
        Assert.True(deviceOnB.CanSendToMe);
        Assert.Single(await DevicesOfAsync(a));
        Assert.Single(await DevicesOfAsync(b));
        Assert.Contains(a.Presence.Devices, d => d.Id == b.Identity.Identity.Id && d.IsPaired);

        // The pinned keys now open a paired session.
        var incoming = TestCores.NextIncomingAsync(a);
        await using var connection = await TestCores.ConnectAsync(b, a, deviceOnB, Ct);
        await using var answer = await incoming;
        Assert.Equal(PeerAccess.Paired, connection.Access);
        Assert.Equal(PeerAccess.Paired, answer.Access);
    }

    [Fact]
    public async Task Device_found_on_the_lan_pairs_with_the_code()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var request = NextRequestAsync(b);

        var onA = await PairOverLanAsync(a, b);
        var onB = await request;

        Assert.False(onB.ViaInvitation);
        Assert.Equal(onA.SecurityCode, onB.SecurityCode);
        var (storedOnB, storedOnA) = await ConfirmBothAsync(onB, onA);
        Assert.Equal(b.Identity.Identity.Id, storedOnA.Id);
        Assert.Equal(a.Identity.Identity.Id, storedOnB.Id);
    }

    [Fact]
    public async Task Codes_differ_cancels_on_both_sides_and_stores_nothing()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var request = NextRequestAsync(b);
        var onA = await PairOverLanAsync(a, b);
        var onB = await request;

        await onA.ConfirmAsync(Ct);
        await onB.CancelAsync();

        var here = await Assert.ThrowsAsync<PairingCanceledException>(() => onB.Completion);
        Assert.False(here.ByOtherDevice);
        var there = await Assert.ThrowsAsync<PairingCanceledException>(() => onA.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(there.ByOtherDevice);
        Assert.Equal("the security codes differ", there.Reason);
        Assert.Empty(await DevicesOfAsync(a));
        Assert.Empty(await DevicesOfAsync(b));
    }

    [Fact]
    public async Task Pairing_not_confirmed_in_time_stores_nothing()
    {
        var quick = new PairingOptions { ConfirmationTimeout = TimeSpan.FromSeconds(1) };
        var a = await StartAsync("laptop", quick);
        var b = await StartAsync("workstation", quick);
        var request = NextRequestAsync(b);
        var onA = await PairOverLanAsync(a, b);
        var onB = await request;

        await onA.ConfirmAsync(Ct);

        var error = await Assert.ThrowsAsync<PairingCanceledException>(() => onB.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Contains("not confirmed", error.Reason);
        await Assert.ThrowsAsync<PairingCanceledException>(() => onA.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Empty(await DevicesOfAsync(a));
        Assert.Empty(await DevicesOfAsync(b));
    }

    [Fact]
    public async Task Invitation_can_be_used_only_once()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var c = await StartAsync("tablet");
        var request = NextRequestAsync(a);
        var invitation = a.Pairing.CreateInvitation();

        var first = await b.Pairing.PairAsync(b.Pairing.ReadInvitation(invitation.Text), Ct);
        await (await request).CancelAsync("closed");
        await Assert.ThrowsAsync<PairingCanceledException>(() => first.Completion);

        var error = await Assert.ThrowsAsync<PairingCanceledException>(() => c.Pairing.PairAsync(c.Pairing.ReadInvitation(invitation.Text), Ct));
        Assert.True(error.ByOtherDevice);
        Assert.Contains("already used", error.Reason);
    }

    [Fact]
    public async Task Revoked_invitation_is_refused()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        _ = NextRequestAsync(a);
        var invitation = a.Pairing.CreateInvitation();
        a.Pairing.RevokeInvitation(invitation);

        await Assert.ThrowsAsync<PairingCanceledException>(() => b.Pairing.PairAsync(b.Pairing.ReadInvitation(invitation.Text), Ct));
    }

    [Fact]
    public async Task Invitation_answered_by_another_key_fails()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var impostor = await StartAsync("impostor");
        var received = b.Pairing.ReadInvitation(a.Pairing.CreateInvitation().Text);

        await Assert.ThrowsAsync<PairingException>(() =>
            b.Pairing.PairAsync(received with { Port = impostor.Lan.Port }, Ct));
    }

    [Fact]
    public async Task Another_device_at_the_announced_address_is_not_paired()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        _ = NextRequestAsync(b);

        var error = await Assert.ThrowsAsync<PairingException>(() =>
            a.Pairing.PairAsync(Guid.NewGuid(), [IPAddress.Loopback], b.Lan.Port, Ct));
        Assert.Contains("Another device answered", error.Message);
    }

    [Fact]
    public async Task Device_without_a_pairing_screen_refuses()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");

        var error = await Assert.ThrowsAsync<PairingCanceledException>(() => PairOverLanAsync(a, b));
        Assert.Equal("the other device is not ready to pair", error.Reason);
    }

    [Fact]
    public async Task Second_request_while_pairing_is_refused()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var stranger = await StartAsync("stranger");
        var request = NextRequestAsync(b);
        await using var first = await PairOverLanAsync(a, b);
        await using var onB = await request;

        var error = await Assert.ThrowsAsync<PairingCanceledException>(() => PairOverLanAsync(stranger, b));
        Assert.Contains("already pairing", error.Reason);

        await onB.CancelAsync();
        await Assert.ThrowsAsync<PairingCanceledException>(() => first.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        var again = NextRequestAsync(b);
        await using var retry = await PairOverLanAsync(stranger, b);
        Assert.Equal("stranger", (await again).RemoteName);
    }

    [Fact]
    public async Task Pairing_again_after_a_removal_on_one_side()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("workstation");
        var request = NextRequestAsync(b);
        var (_, deviceOnB) = await ConfirmBothAsync(await PairOverLanAsync(a, b), await request);

        // a removes b; b still knows a. a starts pairing again.
        await using (var db = await a.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync(Ct))
            await db.Devices.ExecuteDeleteAsync(Ct);
        await a.Presence.ReloadPairedDevicesAsync(Ct);
        var retry = NextRequestAsync(b);

        var onA = await PairOverLanAsync(a, b);
        var onB = await retry;
        await ConfirmBothAsync(onA, onB);

        Assert.Single(await DevicesOfAsync(a));
        var stillOnB = Assert.Single(await DevicesOfAsync(b));
        Assert.Equal(deviceOnB.PublicKey, stillOnB.PublicKey);
        await using var connection = await TestCores.ConnectAsync(a, b, Ct);
        Assert.Equal(PeerAccess.Paired, connection.Access);
    }
}
