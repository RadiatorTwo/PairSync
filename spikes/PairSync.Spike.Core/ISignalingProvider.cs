using PairSync.Transport;

namespace PairSync.Spike;

/// <summary>
/// Carries session descriptions between two devices (plan §6): manual copy-and-paste or files,
/// used by the spike only; the app uses signed codes and ISignalingChannel.
/// </summary>
public interface ISignalingProvider
{
    Task PublishOfferAsync(SessionDescription offer, CancellationToken cancellationToken);

    Task<SessionDescription> ReceiveOfferAsync(CancellationToken cancellationToken);

    Task PublishAnswerAsync(SessionDescription answer, CancellationToken cancellationToken);

    Task<SessionDescription> ReceiveAnswerAsync(CancellationToken cancellationToken);
}
