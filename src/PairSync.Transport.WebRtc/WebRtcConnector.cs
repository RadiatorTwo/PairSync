namespace PairSync.Transport.WebRtc;

/// <summary>
/// WebRTC data channel transport on top of libdatachannel. Signaling is non-trickle: each side
/// waits for ICE gathering to finish so one offer and one answer carry all candidates, which
/// suits manual exchange by file, clipboard or QR code.
/// </summary>
public sealed class WebRtcConnector(TransportOptions options) : ITransportConnector
{
    public async Task<(ITransportSession Session, SessionDescription Offer)> CreateOfferAsync(
        IReadOnlyList<string> channelLabels, CancellationToken cancellationToken)
    {
        if (channelLabels.Count == 0)
            throw new ArgumentException("At least one channel is required.", nameof(channelLabels));

        var session = WebRtcSession.Create(options);
        try
        {
            // Creating the first channel starts negotiation and ICE gathering.
            foreach (var label in channelLabels)
                session.CreateChannel(label);
            var offer = await session.GetCompleteLocalDescriptionAsync(SessionDescriptionType.Offer, cancellationToken)
                .ConfigureAwait(false);
            return (session, offer);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<(ITransportSession Session, SessionDescription Answer)> AcceptOfferAsync(
        SessionDescription offer, CancellationToken cancellationToken)
    {
        if (offer.Type != SessionDescriptionType.Offer)
            throw new ArgumentException("Expected an offer.", nameof(offer));

        var session = WebRtcSession.Create(options);
        try
        {
            // Setting the remote offer makes libdatachannel create the answer and start gathering.
            session.SetRemoteDescription(offer);
            var answer = await session.GetCompleteLocalDescriptionAsync(SessionDescriptionType.Answer, cancellationToken)
                .ConfigureAwait(false);
            return (session, answer);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
