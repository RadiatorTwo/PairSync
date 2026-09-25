using System.Security.Cryptography;
using PairSync.Spike;
using PairSync.Transport;

namespace PairSync.TransportTests;

public sealed class TransportTests
{
    private static CancellationToken Timeout(int seconds = 60) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    [Theory]
    [MemberData(nameof(LoopbackPair.Transports), MemberType = typeof(LoopbackPair))]
    public async Task Channels_open_on_both_sides_and_route_is_host(SpikeTransport transport)
    {
        var ct = Timeout();
        await using var pair = await LoopbackPair.ConnectAsync(transport, ct);

        foreach (var label in LoopbackPair.Channels)
        {
            var a = await pair.Offerer.GetChannelAsync(label, ct);
            var b = await pair.Answerer.GetChannelAsync(label, ct);
            Assert.True(a.IsOpen);
            Assert.True(b.IsOpen);
            Assert.Equal(label, b.Label);
        }

        Assert.Equal(TransportState.Connected, pair.Offerer.State);
        Assert.NotNull(pair.Offerer.Route);
        Assert.True(pair.Offerer.Route!.IsDirectLan, pair.Offerer.Route.ToString());
    }

    [Theory]
    [MemberData(nameof(LoopbackPair.Transports), MemberType = typeof(LoopbackPair))]
    public async Task Messages_arrive_in_order_and_intact(SpikeTransport transport)
    {
        var ct = Timeout();
        await using var pair = await LoopbackPair.ConnectAsync(transport, ct);
        var sender = await pair.Offerer.GetChannelAsync("data", ct);
        var receiver = await pair.Answerer.GetChannelAsync("data", ct);
        Assert.True(sender.MaxMessageSize >= 256 * 1024 - 1024, $"max message size {sender.MaxMessageSize}");

        const int count = 200;
        var size = sender.MaxMessageSize;
        var sent = Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetBytes(size)).ToArray();
        var maxBuffered = 0;
        var send = Task.Run(async () =>
        {
            foreach (var message in sent)
            {
                await sender.SendAsync(message, ct);
                maxBuffered = Math.Max(maxBuffered, sender.BufferedAmount);
            }
        }, ct);

        var received = 0;
        await foreach (var message in receiver.ReadAllAsync(ct))
        {
            Assert.True(message.Span.SequenceEqual(sent[received]), $"message {received} differs");
            if (++received == count)
                break;
        }

        await send;
        Assert.Equal(count, received);
        // Backpressure: the queue never grows beyond the high watermark plus the message just sent.
        Assert.InRange(maxBuffered, 0, LoopbackPair.Options.SendHighWatermark + size);
    }

    /// <summary>Needs Internet access and UDP to a public STUN server, therefore explicit.</summary>
    [Fact(Explicit = true)]
    public async Task Stun_adds_server_reflexive_candidates_to_the_offer()
    {
        var ct = Timeout();
        var connector = new PairSync.Transport.WebRtc.WebRtcConnector(new TransportOptions { IceServers = ["stun:stun.cloudflare.com:3478"] });

        var (session, offer) = await connector.CreateOfferAsync(["control"], ct);
        await session.DisposeAsync();

        Assert.Contains("typ host", offer.Sdp);
        Assert.Contains("typ srflx", offer.Sdp);
    }

    [Theory]
    [MemberData(nameof(LoopbackPair.Transports), MemberType = typeof(LoopbackPair))]
    public async Task Oversized_message_is_rejected(SpikeTransport transport)
    {
        var ct = Timeout();
        await using var pair = await LoopbackPair.ConnectAsync(transport, ct);
        var sender = await pair.Offerer.GetChannelAsync("data", ct);
        await Assert.ThrowsAsync<TransportException>(() => sender.SendAsync(new byte[sender.MaxMessageSize + 1], ct).AsTask());
    }
}
