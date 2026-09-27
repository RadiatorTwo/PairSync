namespace PairSync.Transport;

/// <summary>
/// Creates sessions through an explicit offer/answer exchange. How the descriptions travel
/// between devices is up to an <see cref="ISignalingProvider"/>.
/// </summary>
public interface ITransportConnector
{
    /// <summary>Starts an outgoing session and returns it together with the complete offer.</summary>
    Task<(ITransportSession Session, SessionDescription Offer)> CreateOfferAsync(
        IReadOnlyList<string> channelLabels, CancellationToken cancellationToken);

    /// <summary>Accepts a remote offer and returns the session together with the complete answer.</summary>
    Task<(ITransportSession Session, SessionDescription Answer)> AcceptOfferAsync(
        SessionDescription offer, CancellationToken cancellationToken);
}
