namespace PairSync.Transport;

/// <summary>
/// Carries session descriptions between two devices (plan §6): manual copy-and-paste or files,
/// LAN signaling, or later an optional rendezvous service.
/// </summary>
public interface ISignalingProvider
{
    Task PublishOfferAsync(SessionDescription offer, CancellationToken cancellationToken);

    Task<SessionDescription> ReceiveOfferAsync(CancellationToken cancellationToken);

    Task PublishAnswerAsync(SessionDescription answer, CancellationToken cancellationToken);

    Task<SessionDescription> ReceiveAnswerAsync(CancellationToken cancellationToken);
}
