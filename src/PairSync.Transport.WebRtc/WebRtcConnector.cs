namespace PairSync.Transport.WebRtc;

/// <summary>
/// WebRTC data channel transport on top of libdatachannel. Signaling is non-trickle: each side
/// waits for ICE gathering to finish so one offer and one answer carry all candidates, which
/// suits manual exchange by file, clipboard or QR code.
/// </summary>
/// <remarks>
/// Manual exchange means the answer may take minutes to travel back. The offer therefore makes the
/// answering side the DTLS server (<see cref="SdpInspector.WithActiveSetup"/>), and the bundled libjuice
/// keeps ICE checks alive for up to 10 minutes; <see cref="TransportOptions.AnswerTimeout"/> and
/// <see cref="TransportOptions.ConnectTimeout"/> set the actual deadlines.
/// </remarks>
public sealed class WebRtcConnector(TransportOptions options) : ITransportConnector
{
    public async Task<(WebRtcSession Session, SessionDescription Offer)> CreateOfferAsync(
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
            return (session, offer with { Sdp = SdpInspector.WithActiveSetup(offer.Sdp) });
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates the answer to <paramref name="offer"/>. The session then waits up to
    /// <see cref="TransportOptions.AnswerTimeout"/> for the other side to apply the answer.
    /// </summary>
    /// <param name="remotePublicKey">Device key that signed the offer, if the caller verified it; see <see cref="WebRtcSession.BindRemotePublicKey"/>.</param>
    public async Task<(WebRtcSession Session, SessionDescription Answer)> AcceptOfferAsync(
        SessionDescription offer, byte[]? remotePublicKey, CancellationToken cancellationToken)
    {
        if (offer.Type != SessionDescriptionType.Offer)
            throw new ArgumentException("Expected an offer.", nameof(offer));

        var session = WebRtcSession.Create(options);
        try
        {
            if (remotePublicKey is not null)
                session.BindRemotePublicKey(remotePublicKey);
            // Setting the remote offer makes libdatachannel create the answer and start gathering.
            session.SetRemoteDescription(offer);
            session.ArmConnectDeadline(options.AnswerTimeout);
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

    public Task<(WebRtcSession Session, SessionDescription Answer)> AcceptOfferAsync(
        SessionDescription offer, CancellationToken cancellationToken) =>
        AcceptOfferAsync(offer, remotePublicKey: null, cancellationToken);

    async Task<(ITransportSession Session, SessionDescription Offer)> ITransportConnector.CreateOfferAsync(
        IReadOnlyList<string> channelLabels, CancellationToken cancellationToken) =>
        await CreateOfferAsync(channelLabels, cancellationToken).ConfigureAwait(false);

    async Task<(ITransportSession Session, SessionDescription Answer)> ITransportConnector.AcceptOfferAsync(
        SessionDescription offer, CancellationToken cancellationToken) =>
        await AcceptOfferAsync(offer, cancellationToken).ConfigureAwait(false);
}
