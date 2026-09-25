using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Connections;
using PairSync.Application.Pairing;
using PairSync.Application.Presence;
using PairSync.Domain;
using PairSync.Storage;

namespace PairSync.IntegrationTests;

/// <summary>Application cores for tests: own data directory, listener on a free loopback-reachable port.</summary>
internal static class TestCores
{
    /// <param name="presence">mDNS settings; off unless given, so tests do not announce themselves on the network.</param>
    /// <param name="pairing">Pairing settings; invitations point to loopback unless given.</param>
    public static Task<PairSyncCore> StartAsync(
        DataDirectory data, CancellationToken cancellationToken, PresenceOptions? presence = null, PairingOptions? pairing = null) =>
        PairSyncCore.StartAsync(data, cancellationToken, services =>
        {
            services.AddSingleton(new LanOptions { Port = 0 });
            services.AddSingleton(presence ?? new PresenceOptions { Enabled = false });
            services.AddSingleton(pairing ?? new PairingOptions { InvitationAddresses = [IPAddress.Loopback] });
        });

    /// <summary>How <paramref name="other"/> is recorded in the device list of <paramref name="owner"/>.</summary>
    public static PairedDevice DeviceEntryFor(PairSyncCore other) => new()
    {
        Id = other.Identity.Identity.Id,
        Name = other.Settings.Current.EffectiveDeviceName,
        PublicKey = other.Identity.Identity.PublicKey,
        PairedAtUtc = DateTime.UtcNow,
    };

    /// <summary>Stores <paramref name="device"/> in the device list of <paramref name="owner"/>, skipping the pairing steps.</summary>
    public static async Task AddDeviceAsync(PairSyncCore owner, PairedDevice device, CancellationToken cancellationToken)
    {
        await using var db = await owner.Services.GetRequiredService<IDbContextFactory<PairSyncDbContext>>().CreateDbContextAsync(cancellationToken);
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task PairAsync(PairSyncCore a, PairSyncCore b, CancellationToken cancellationToken)
    {
        await AddDeviceAsync(a, DeviceEntryFor(b), cancellationToken);
        await AddDeviceAsync(b, DeviceEntryFor(a), cancellationToken);
    }

    public static Task<PeerConnection> ConnectAsync(PairSyncCore from, PairSyncCore to, CancellationToken cancellationToken) =>
        ConnectAsync(from, to, DeviceEntryFor(to), cancellationToken);

    public static Task<PeerConnection> ConnectAsync(PairSyncCore from, PairSyncCore to, PairedDevice expected, CancellationToken cancellationToken) =>
        from.Lan.ConnectAsync(expected, [IPAddress.Loopback], to.Lan.Port, cancellationToken);

    /// <summary>Completes with the next incoming connection of <paramref name="core"/>, which the test then owns.</summary>
    public static Task<PeerConnection> NextIncomingAsync(PairSyncCore core)
    {
        var next = new TaskCompletionSource<PeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Lan.IncomingConnectionHandler = connection =>
        {
            if (!next.TrySetResult(connection))
                return connection.DisposeAsync().AsTask();
            return Task.CompletedTask;
        };
        return next.Task;
    }
}
