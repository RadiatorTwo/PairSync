using PairSync.Spike;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.TransportTests;

/// <summary>Two sessions in one process over loopback (WebRTC with host candidates only, or TLS on 127.0.0.1).</summary>
internal sealed class LoopbackPair : IAsyncDisposable
{
    public static readonly string[] Channels = SpikeConsole.ChannelLabels;

    public static readonly TheoryData<SpikeTransport> Transports = [SpikeTransport.WebRtc, SpikeTransport.Tls];

    private LoopbackPair(ITransportSession offerer, ITransportSession answerer)
    {
        Offerer = offerer;
        Answerer = answerer;
    }

    public ITransportSession Offerer { get; }

    public ITransportSession Answerer { get; }

    public static TransportOptions Options { get; } = new() { ConnectTimeout = TimeSpan.FromSeconds(20) };

    public static async Task<LoopbackPair> ConnectAsync(SpikeTransport transport, CancellationToken cancellationToken)
    {
        var (sender, receiver) = await ChannelBenchmark.ConnectPairAsync(transport, Options, cancellationToken);
        return new LoopbackPair(sender, receiver);
    }

    /// <summary>Runs the Hello handshake on both sessions (every peer trusted, as in the spike).</summary>
    public async Task<(PeerChannels Offerer, PeerChannels Answerer)> HandshakeAsync(CancellationToken cancellationToken)
    {
        var answerer = SpikePeer.RespondAsync(Answerer, cancellationToken);
        var offerer = await SpikePeer.InitiateAsync(Offerer, cancellationToken);
        return (offerer, await answerer);
    }

    public async ValueTask DisposeAsync()
    {
        await Offerer.DisposeAsync();
        await Answerer.DisposeAsync();
    }
}
