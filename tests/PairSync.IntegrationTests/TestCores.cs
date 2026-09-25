using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Application.Connections;
using PairSync.Domain;
using PairSync.Storage;

namespace PairSync.IntegrationTests;

/// <summary>Application cores for tests: own data directory, listener on a free loopback-reachable port.</summary>
internal static class TestCores
{
    public static Task<PairSyncCore> StartAsync(DataDirectory data, CancellationToken cancellationToken) =>
        PairSyncCore.StartAsync(data, cancellationToken, services => services.AddSingleton(new LanOptions { Port = 0 }));

    /// <summary>How <paramref name="other"/> is recorded in the device list of <paramref name="owner"/>.</summary>
    public static PairedDevice DeviceEntryFor(PairSyncCore other) => new()
    {
        Id = other.Identity.Identity.Id,
        Name = other.Settings.Current.EffectiveDeviceName,
        PublicKey = other.Identity.Identity.PublicKey,
        PairedAtUtc = DateTime.UtcNow,
    };

    /// <summary>Stores <paramref name="device"/> in the device list of <paramref name="owner"/> (pairing comes with work package E).</summary>
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
