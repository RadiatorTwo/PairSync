using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Application.Internet;
using PairSync.Application.Presence;
using PairSync.Application.Transfers;
using PairSync.Domain;
using PairSync.Storage;

namespace PairSync.Application.Devices;

/// <summary>
/// The paired devices and what they may do (plan §11, work package E "Rechte nach der Kopplung"): "may send to me",
/// blocked, removed. A removed device has to pair again.
/// </summary>
public sealed class DeviceService(
    IDbContextFactory<PairSyncDbContext> contexts, PresenceService presence, TransferService transfers, InternetLinkService internet,
    ILogger<DeviceService> logger)
{
    /// <summary>A device was blocked, unblocked, removed or its permissions changed. Pairing is reported by presence.</summary>
    public event Action? Changed;

    /// <summary>Paired devices by name.</summary>
    public async Task<IReadOnlyList<PairedDevice>> GetPairedAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.PairedAtUtc)];
    }

    /// <summary>"May send to me". Incoming transfers are still confirmed one by one.</summary>
    public Task SetCanSendToMeAsync(Guid deviceId, bool allowed, CancellationToken cancellationToken) =>
        UpdateAsync(deviceId, d => d.CanSendToMe = allowed, cancellationToken);

    /// <summary>
    /// A blocked device can neither connect nor receive; its unfinished jobs are paused, so a running transfer stops,
    /// and its internet connection is closed.
    /// Unblocking lets the paused jobs be resumed by hand.
    /// </summary>
    public async Task SetBlockedAsync(Guid deviceId, bool blocked, CancellationToken cancellationToken)
    {
        await UpdateAsync(deviceId, d => d.Trust = blocked ? DeviceTrust.Blocked : DeviceTrust.Active, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Device {DeviceId} {Action}", deviceId, blocked ? "blocked" : "unblocked");
        if (!blocked)
            return;
        await internet.CloseAsync(deviceId).ConfigureAwait(false);
        foreach (var job in await JobsOfAsync(deviceId, cancellationToken).ConfigureAwait(false))
        {
            if (job.State is not JobState.Paused)
                await transfers.PauseAsync(job.Id).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels the device's unfinished jobs (telling it if it is reachable) and forgets its key.</summary>
    public async Task RemoveAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        foreach (var job in await JobsOfAsync(deviceId, cancellationToken).ConfigureAwait(false))
            await transfers.CancelAsync(job.Id).ConfigureAwait(false);
        await internet.CloseAsync(deviceId).ConfigureAwait(false);

        await using (var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            await db.Devices.Where(d => d.Id == deviceId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Device {DeviceId} removed", deviceId);
        await presence.ReloadPairedDevicesAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private async Task<IEnumerable<JobView>> JobsOfAsync(Guid deviceId, CancellationToken cancellationToken) =>
        (await transfers.GetJobsAsync(cancellationToken).ConfigureAwait(false)).Where(j => j.PeerDeviceId == deviceId);

    private async Task UpdateAsync(Guid deviceId, Action<PairedDevice> change, CancellationToken cancellationToken)
    {
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The device is not paired.", nameof(deviceId));
            change(device);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await presence.ReloadPairedDevicesAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }
}
