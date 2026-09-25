namespace PairSync.Transport;

/// <summary>Supplies optional TURN relays (plan §2C, after the MVP). Relays only ever see encrypted packets.</summary>
public interface IRelayConfigurationProvider
{
    /// <summary>TURN URIs including credentials, e.g. <c>turn:user:pass@relay.example.org:3478</c>. Empty if none.</summary>
    Task<IReadOnlyList<string>> GetRelayServersAsync(CancellationToken cancellationToken);
}
