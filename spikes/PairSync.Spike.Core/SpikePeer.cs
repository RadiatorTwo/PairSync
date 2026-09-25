using System.Security.Cryptography.X509Certificates;
using PairSync.Domain;
using PairSync.Protocol;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.Spike;

/// <summary>
/// A throwaway device identity per process and a handshake that trusts every peer. The spike has no pairing: the
/// sender pins the receiver's key through the connection code, the receiver accepts whoever connects.
/// </summary>
public static class SpikePeer
{
    private static readonly Lazy<DeviceIdentity> Identity = new(DeviceIdentity.Create);
    private static readonly Lazy<X509Certificate2> LazyCertificate = new(() => Identity.Value.CreateCertificate(TimeProvider.System));

    public static X509Certificate2 Certificate => LazyCertificate.Value;

    private static LocalDevice Local => new(Identity.Value.Id, Environment.MachineName);

    private static ValueTask<AccessDecision> TrustAll<T>(T _) => ValueTask.FromResult(AccessDecision.Grant(PeerAccess.Paired));

    public static async Task<PeerChannels> InitiateAsync(ITransportSession session, CancellationToken cancellationToken) =>
        (await SessionHandshake.InitiateAsync(session, Local, TrustAll, cancellationToken).ConfigureAwait(false)).Channels;

    public static async Task<PeerChannels> RespondAsync(ITransportSession session, CancellationToken cancellationToken) =>
        (await SessionHandshake.RespondAsync(session, Local, TrustAll, cancellationToken).ConfigureAwait(false)).Channels;
}
