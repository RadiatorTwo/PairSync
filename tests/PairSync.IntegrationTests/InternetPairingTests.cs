using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Connections;
using PairSync.Application.Internet;
using PairSync.Application.Pairing;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.Storage;

namespace PairSync.IntegrationTests;

/// <summary>
/// Pairing over the internet (phase 2 block D): internet invitation, answer code, security code over WebRTC on this
/// machine. Invitations carry no LAN address unless a test asks for one, so the LAN path is not available.
/// </summary>
public sealed class InternetPairingTests : IAsyncLifetime
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-internet-pairing-");
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

    private async Task<PairSyncCore> StartAsync(string name, IReadOnlyList<IPAddress>? lanAddresses = null)
    {
        var data = new DataDirectory(Path.Combine(_root.FullName, name));
        var core = await TestCores.StartAsync(data, Ct,
            pairing: new PairingOptions { InvitationAddresses = lanAddresses ?? [] },
            transfers: TestCores.ForTests(data),
            internet: new InternetOptions
            {
                DetectNat = false,
                PingInterval = TimeSpan.FromMilliseconds(500),
                DisconnectGracePeriod = TimeSpan.FromSeconds(2),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });
        core.Settings.Update(s => s with { DeviceName = name });
        _cores.Add(core);
        return core;
    }

    private static Guid Id(PairSyncCore core) => core.Identity.Identity.Id;

    private static Task<PairingSession> NextRequestAsync(PairSyncCore core)
    {
        var next = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Pairing.IncomingRequest += session => next.TrySetResult(session);
        return next.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    private static async Task<List<PairedDevice>> DevicesOfAsync(PairSyncCore core)
    {
        await using var db = await core.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync(Ct);
        return await db.Devices.AsNoTracking().ToListAsync(Ct);
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

    /// <summary>A invites over the internet, B answers, A applies the answer; returns both pairing sessions.</summary>
    private static async Task<(PairingSession OnA, PairingSession OnB)> StartPairingAsync(PairSyncCore a, PairSyncCore b)
    {
        var request = NextRequestAsync(a);
        var invitation = await a.Pairing.CreateInvitationAsync(Ct, overInternet: true);
        var pairing = await b.Pairing.AcceptInvitationAsync(b.Pairing.ReadInvitation(invitation.Text), Ct);
        Assert.NotNull(pairing.Answer);

        var answered = await a.Pairing.ApplyInvitationAnswerAsync(pairing.Answer.Text, Ct);
        Assert.Equal(Id(b), answered.DeviceId);
        return (await request, await pairing.Session.WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task Internet_invitation_pairs_and_the_connection_stays_as_link()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("office-pc");

        var invitation = await a.Pairing.CreateInvitationAsync(Ct, overInternet: true);
        Assert.StartsWith("PSI2:", invitation.Text);
        var received = b.Pairing.ReadInvitation(invitation.Text);
        Assert.NotNull(received.Offer);
        Assert.Empty(received.Addresses);

        var request = NextRequestAsync(a);
        var pairing = await b.Pairing.AcceptInvitationAsync(received, Ct);
        Assert.StartsWith("PSR1:", pairing.Answer!.Text);
        Assert.False(pairing.Session.IsCompleted, "B waits for A to apply the answer");

        await a.Pairing.ApplyInvitationAnswerAsync(pairing.Answer.Text, Ct);
        var onA = await request;
        var onB = await pairing.Session.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(onA.SecurityCode, onB.SecurityCode);
        Assert.True(onA.ViaInvitation);
        Assert.True(onA.IsIncoming);
        Assert.Equal("office-pc", onA.RemoteName);
        Assert.Equal(a.Identity.Identity.Fingerprint, onB.RemoteFingerprint);
        Assert.Null(a.Internet.LinkTo(Id(b)));
        Assert.Empty(await DevicesOfAsync(a));

        await onA.ConfirmAsync(Ct);
        await onB.ConfirmAsync(Ct);
        await onA.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await onB.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Single(await DevicesOfAsync(a));
        Assert.Single(await DevicesOfAsync(b));
        await WaitAsync(() => a.Internet.LinkTo(Id(b)) is not null && b.Internet.LinkTo(Id(a)) is not null, "no link after pairing");
        Assert.Equal(InternetLinkPhase.Connected, a.Internet.StatusOf(Id(b))!.Phase);
        Assert.Equal(PeerRoute.Internet, b.Links.RouteTo(Id(a)));

        // The link carries jobs right away, without a connection code.
        var file = Path.Combine(_root.FullName, "hello.bin");
        await File.WriteAllBytesAsync(file, RandomNumberGenerator.GetBytes(50_000), Ct);
        var job = await a.Transfers.SendAsync(Id(b), SendScanner.Scan([file], Ct), ExistingFilePolicy.KeepBoth, suggestedFolder: null, Ct);
        await WaitAsync(() => b.Transfers.GetHistoryAsync(50, Ct).Result.Any(h => h.JobId == job && h.Outcome == HistoryOutcome.Completed),
            "job did not complete over the new link", 60);
        Assert.Equal(await File.ReadAllBytesAsync(file, Ct),
            await File.ReadAllBytesAsync(Path.Combine(b.DataDirectory.Root, "downloads", "PairSync", "laptop", "hello.bin"), Ct));
    }

    [Fact]
    public async Task Canceled_internet_pairing_stores_nothing_and_closes_the_connection()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("office-pc");
        var (onA, onB) = await StartPairingAsync(a, b);
        Assert.True(a.Internet.HasPairingConnection(Id(b)));

        await onA.ConfirmAsync(Ct);
        await onB.CancelAsync();

        await Assert.ThrowsAsync<PairingCanceledException>(() => onB.Completion);
        await Assert.ThrowsAsync<PairingCanceledException>(() => onA.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Empty(await DevicesOfAsync(a));
        Assert.Empty(await DevicesOfAsync(b));
        await WaitAsync(() => !a.Internet.HasPairingConnection(Id(b)) && !b.Internet.HasPairingConnection(Id(a)), "connection still open");
        Assert.Null(a.Internet.LinkTo(Id(b)));
        Assert.Null(b.Internet.LinkTo(Id(a)));
    }

    [Fact]
    public async Task Invitation_and_answer_work_once()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("office-pc");
        var request = NextRequestAsync(a);
        var invitation = await a.Pairing.CreateInvitationAsync(Ct, overInternet: true);
        var received = b.Pairing.ReadInvitation(invitation.Text);
        var pairing = await b.Pairing.AcceptInvitationAsync(received, Ct);

        var again = await Assert.ThrowsAsync<InvalidInvitationException>(() => b.Pairing.AcceptInvitationAsync(received, Ct));
        Assert.Contains("already answered", again.Message);

        await a.Pairing.ApplyInvitationAnswerAsync(pairing.Answer!.Text, Ct);
        await Assert.ThrowsAsync<InvalidInvitationException>(() => a.Pairing.ApplyInvitationAnswerAsync(pairing.Answer.Text, Ct));

        var onA = await request;
        var onB = await pairing.Session.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await onA.ConfirmAsync(Ct);
        await onB.ConfirmAsync(Ct);
        await onA.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Answer_to_a_revoked_invitation_is_refused()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("office-pc");
        var invitation = await a.Pairing.CreateInvitationAsync(Ct, overInternet: true);
        var pairing = await b.Pairing.AcceptInvitationAsync(b.Pairing.ReadInvitation(invitation.Text), Ct);

        a.Pairing.RevokeInvitation(invitation);

        var refused = await Assert.ThrowsAsync<InvalidInvitationException>(() => a.Pairing.ApplyInvitationAnswerAsync(pairing.Answer!.Text, Ct));
        Assert.Contains("open invitation", refused.Message);
    }

    [Fact]
    public async Task Answer_code_on_the_invited_device_is_refused()
    {
        var a = await StartAsync("laptop");
        var b = await StartAsync("office-pc");
        var invitation = await a.Pairing.CreateInvitationAsync(Ct, overInternet: true);
        var pairing = await b.Pairing.AcceptInvitationAsync(b.Pairing.ReadInvitation(invitation.Text), Ct);

        var own = await Assert.ThrowsAsync<InvalidInvitationException>(() => b.Pairing.ApplyInvitationAnswerAsync(pairing.Answer!.Text, Ct));
        Assert.Contains("this device", own.Message);
    }

    [Fact]
    public async Task Internet_invitation_pairs_over_the_lan_when_reachable()
    {
        var a = await StartAsync("laptop", lanAddresses: [IPAddress.Loopback]);
        var b = await StartAsync("office-pc");
        var request = NextRequestAsync(a);

        var invitation = await a.Pairing.CreateInvitationAsync(Ct, overInternet: true);
        var pairing = await b.Pairing.AcceptInvitationAsync(b.Pairing.ReadInvitation(invitation.Text), Ct);

        Assert.Null(pairing.Answer);
        var onB = await pairing.Session;
        var onA = await request;
        Assert.Equal(onA.SecurityCode, onB.SecurityCode);
        await onA.ConfirmAsync(Ct);
        await onB.ConfirmAsync(Ct);
        await onB.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(b.Internet.LinkTo(Id(a)));
        Assert.False(b.Internet.HasPairingConnection(Id(a)));
    }

    [Fact]
    public async Task Without_stun_servers_invitations_stay_lan_only()
    {
        var a = await StartAsync("laptop", lanAddresses: [IPAddress.Loopback]);

        Assert.False(a.Internet.CanInviteOverInternet);
        var invitation = await a.Pairing.CreateInvitationAsync(Ct);

        Assert.StartsWith("PSI1:", invitation.Text);
    }
}
