using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.SyncEngine;

namespace PairSync.Application.Connections;

/// <summary>Result of checking a peer: the access level (or refusal) and the paired device, if it is one.</summary>
public sealed record PeerAuthorization(AccessDecision Decision, PairedDevice? Device);

/// <summary>
/// Decides what a peer may do, based on the key it proved in the TLS handshake (plan §5, §11):
/// paired and active → <see cref="PeerAccess.Paired"/>; unknown key → <see cref="PeerAccess.PairingOnly"/>;
/// blocked, or a device id that does not belong to the key → refused.
/// </summary>
public sealed class PeerAuthorizer(IDbContextFactory<PairSyncDbContext> contexts, TimeProvider time, ILogger<PeerAuthorizer> logger)
{
    public async Task<PeerAuthorization> AuthorizeAsync(byte[]? remoteKey, Guid claimedDeviceId, CancellationToken cancellationToken)
    {
        if (remoteKey is null || remoteKey.Length == 0)
            return new(AccessDecision.Reject("no device certificate"), null);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.SingleOrDefaultAsync(d => d.PublicKey == remoteKey, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            if (await db.Devices.AnyAsync(d => d.Id == claimedDeviceId, cancellationToken).ConfigureAwait(false))
                logger.LogWarning(
                    "A device claims the id {DeviceId} of a paired device but has another key ({Fingerprint}); treated as unknown",
                    claimedDeviceId, DeviceFingerprint.Of(remoteKey).ToShortString());
            return new(AccessDecision.Grant(PeerAccess.PairingOnly), null);
        }

        if (device.Trust == DeviceTrust.Blocked)
            return new(AccessDecision.Reject("this device is blocked"), device);
        if (device.Id != claimedDeviceId)
        {
            logger.LogWarning("Device {Name} presented its key with the wrong id {ClaimedId}", device.Name, claimedDeviceId);
            return new(AccessDecision.Reject("the device id does not match its key"), device);
        }

        device.LastSeenUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(AccessDecision.Grant(PeerAccess.Paired), device);
    }
}
