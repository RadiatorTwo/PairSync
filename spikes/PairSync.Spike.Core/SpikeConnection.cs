using PairSync.Transport;
using PairSync.Transport.Tls;
using PairSync.Transport.WebRtc;

namespace PairSync.Spike;

public enum SpikeTransport { WebRtc, Tls }

/// <summary>
/// Connection setup for both spike programs. WebRTC: the sender offers, the receiver answers.
/// TLS (LAN): the receiver listens and publishes its addresses and certificate hash, the sender connects.
/// </summary>
public static class SpikeConnection
{
    private const string TlsEndpointFile = "endpoint.pairsync";

    public static async Task<ITransportSession> ConnectAsSenderAsync(
        SpikeTransport transport, TransportOptions options, ManualSignaling signaling, CancellationToken cancellationToken)
    {
        if (transport == SpikeTransport.Tls)
        {
            var code = await signaling.ReceiveCodeAsync(TlsEndpointFile, TlsEndpointInfo.CodePrefix, cancellationToken).ConfigureAwait(false);
            return await TlsConnector.ConnectAsync(TlsEndpointInfo.FromCode(code), SpikePeer.Certificate, SpikeConsole.ChannelLabels, options, cancellationToken)
                .ConfigureAwait(false);
        }

        var (session, offer) = await new WebRtcConnector(options).CreateOfferAsync(SpikeConsole.ChannelLabels, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            SpikeConsole.WarnIfNoPublicCandidate(options, offer);
            await signaling.PublishOfferAsync(offer, cancellationToken).ConfigureAwait(false);
            await session.ApplyAnswerAsync(await signaling.ReceiveAnswerAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
            await session.GetChannelAsync("data", cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<ITransportSession> ConnectAsReceiverAsync(
        SpikeTransport transport, int port, TransportOptions options, ManualSignaling signaling, CancellationToken cancellationToken)
    {
        if (transport == SpikeTransport.Tls)
        {
            using var listener = TlsListener.Start(port, SpikePeer.Certificate, options);
            Console.WriteLine($"Listening on port {listener.Endpoint.Port} ({string.Join(", ", listener.Endpoint.Addresses)})");
            await signaling.PublishCodeAsync(TlsEndpointFile, listener.Endpoint.ToCode(), cancellationToken).ConfigureAwait(false);
            return await listener.AcceptAsync(SpikeConsole.ChannelLabels, cancellationToken).ConfigureAwait(false);
        }

        var offer = await signaling.ReceiveOfferAsync(cancellationToken).ConfigureAwait(false);
        var (session, answer) = await new WebRtcConnector(options).AcceptOfferAsync(offer, cancellationToken).ConfigureAwait(false);
        try
        {
            SpikeConsole.WarnIfNoPublicCandidate(options, answer);
            await signaling.PublishAnswerAsync(answer, cancellationToken).ConfigureAwait(false);
            await session.GetChannelAsync("data", cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
