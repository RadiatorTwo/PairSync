namespace PairSync.Transport;

/// <summary>
/// A long-lived session that carries more channels than the ones it was negotiated with, so one
/// connection can serve many jobs (each job opens its own channels and closes them when done).
/// </summary>
public interface ISessionLink : ITransportSession
{
    /// <summary>Opens a new channel on the connected session and waits until it is open on both sides.</summary>
    Task<IMessageChannel> OpenChannelAsync(string label, CancellationToken cancellationToken);

    /// <summary>
    /// Every channel the other side created, in the order they opened, including the ones negotiated with
    /// the session. Completes when the session fails or closes.
    /// </summary>
    IAsyncEnumerable<IMessageChannel> AcceptChannelsAsync(CancellationToken cancellationToken);

    /// <summary>Closes a channel on both sides and frees its label for reuse.</summary>
    ValueTask CloseChannelAsync(IMessageChannel channel);

    /// <summary>Candidate types both sides offered and the pair in use; basis for NAT diagnostics.</summary>
    IceReport GetIceReport();
}
