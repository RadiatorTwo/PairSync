using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PairSync.Transport;
using PairSync.Transport.WebRtc;

namespace PairSync.TransportTests;

/// <summary>WebRTC as a long-lived internet link: manual-signaling timing, key binding, channels per job.</summary>
public sealed partial class WebRtcLinkTests
{
    private static readonly string[] Labels = ["link"];

    private static CancellationToken Timeout(int seconds = 60) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    private static TransportOptions Options(TimeSpan? connect = null, TimeSpan? answer = null) => new()
    {
        ConnectTimeout = connect ?? TimeSpan.FromSeconds(20),
        AnswerTimeout = answer ?? TimeSpan.FromMinutes(10),
    };

    private static async Task<(WebRtcSession Offerer, WebRtcSession Answerer)> ConnectAsync(CancellationToken ct, TransportOptions? options = null)
    {
        var connector = new WebRtcConnector(options ?? Options());
        var (offerer, offer) = await connector.CreateOfferAsync(Labels, ct);
        var (answerer, answer) = await connector.AcceptOfferAsync(offer, ct);
        await offerer.ApplyAnswerAsync(answer, ct);
        await offerer.GetChannelAsync("link", ct);
        await answerer.GetChannelAsync("link", ct);
        return (offerer, answerer);
    }

    /// <summary>
    /// The answer travels back by hand. Before phase 2 the answering side gave up after ~30 s
    /// (DTLS handshake timeout as client); now it is the DTLS server and waits.
    /// </summary>
    [Theory]
    [InlineData(35)]
    [InlineData(150, Explicit = true)]
    public async Task Answer_applied_long_after_it_was_created_still_connects(int delaySeconds)
    {
        var ct = Timeout(delaySeconds + 60);
        var connector = new WebRtcConnector(Options());
        var (offerer, offer) = await connector.CreateOfferAsync(Labels, ct);
        await using var _ = offerer;
        var (answerer, answer) = await connector.AcceptOfferAsync(offer, ct);
        await using var __ = answerer;

        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
        await offerer.ApplyAnswerAsync(answer, ct);

        var a = await offerer.GetChannelAsync("link", ct);
        var b = await answerer.GetChannelAsync("link", ct);
        await a.SendAsync(new byte[] { 1, 2, 3 }, ct);
        await foreach (var message in b.ReadAllAsync(ct))
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, message.ToArray());
            break;
        }
    }

    /// <summary>
    /// While the answer travels back by hand, the answering side must keep sending checks, or a NAT on its
    /// side forgets the path to the other side and drops the late checks from there. Stock libjuice gives up
    /// after ~40 s; the bundled build keeps going every 5 s (native/vcpkg-ports/libjuice/manual-signaling.patch).
    /// </summary>
    [Fact]
    public async Task Answering_side_keeps_checking_while_it_waits_for_the_other_side()
    {
        var ct = Timeout(90);
        using var sink = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)sink.Client.LocalEndPoint!).Port;

        var connector = new WebRtcConnector(Options());
        var (offerer, offer) = await connector.CreateOfferAsync(Labels, ct);
        await using var _ = offerer;
        // The only candidate points at the sink, which never answers: like a NAT dropping the checks.
        var lines = offer.Sdp.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.StartsWith("a=candidate:", StringComparison.Ordinal)).ToList();
        lines.Insert(lines.FindIndex(l => l.StartsWith("a=end-of-candidates", StringComparison.Ordinal)),
            $"a=candidate:1 1 UDP 2122317823 127.0.0.1 {port} typ host");
        var (answerer, _) = await connector.AcceptOfferAsync(offer with { Sdp = string.Join("\r\n", lines) }, ct);
        await using var __ = answerer;

        var started = DateTime.UtcNow;
        var late = 0;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(55))
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var packet = await sink.ReceiveAsync(wait.Token);
                // STUN Binding request with the magic cookie 0x2112A442.
                if (packet.Buffer.Length >= 20 && packet.Buffer[0] == 0 && packet.Buffer[1] == 1
                    && packet.Buffer[4] == 0x21 && packet.Buffer[5] == 0x12 && packet.Buffer[6] == 0xA4 && packet.Buffer[7] == 0x42
                    && DateTime.UtcNow - started > TimeSpan.FromSeconds(45))
                    late++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }

        Assert.True(late >= 1, "No ICE checks after 45 s: libjuice without manual-signaling.patch?");
        Assert.NotEqual(TransportState.Failed, answerer.State);
    }

    /// <summary>The offering side is the DTLS client, so the answering side waits as the server.</summary>
    [Fact]
    public async Task Offer_carries_active_setup_and_candidates()
    {
        var ct = Timeout();
        var (offerer, offer) = await new WebRtcConnector(Options()).CreateOfferAsync(Labels, ct);
        await offerer.DisposeAsync();

        Assert.Contains("a=setup:active", offer.Sdp);
        Assert.Contains(CandidateType.Host, SdpInspector.CandidateTypes(offer.Sdp));
    }

    [Fact]
    public async Task Tampered_fingerprint_in_the_answer_fails_the_offering_side()
    {
        var ct = Timeout();
        var connector = new WebRtcConnector(Options(connect: TimeSpan.FromSeconds(10)));
        var (offerer, offer) = await connector.CreateOfferAsync(Labels, ct);
        await using var _ = offerer;
        var (answerer, answer) = await connector.AcceptOfferAsync(offer, ct);
        await using var __ = answerer;

        await offerer.ApplyAnswerAsync(answer with { Sdp = TamperFingerprint(answer.Sdp) }, ct);

        await Assert.ThrowsAsync<TransportException>(() => offerer.GetChannelAsync("link", ct));
        Assert.True(offerer.State is TransportState.Failed or TransportState.Closed, offerer.State.ToString());
    }

    [Fact]
    public async Task Tampered_fingerprint_in_the_offer_fails_the_answering_side()
    {
        var ct = Timeout();
        var connector = new WebRtcConnector(Options(connect: TimeSpan.FromSeconds(10), answer: TimeSpan.FromSeconds(10)));
        var (offerer, offer) = await connector.CreateOfferAsync(Labels, ct);
        await using var _ = offerer;
        var (answerer, answer) = await connector.AcceptOfferAsync(offer with { Sdp = TamperFingerprint(offer.Sdp) }, ct);
        await using var __ = answerer;

        await offerer.ApplyAnswerAsync(answer, ct);

        await Assert.ThrowsAsync<TransportException>(() => answerer.GetChannelAsync("link", ct));
        await Assert.ThrowsAsync<TransportException>(() => offerer.GetChannelAsync("link", ct));
    }

    [Fact]
    public async Task Answering_side_fails_when_the_answer_is_never_applied()
    {
        var ct = Timeout();
        var connector = new WebRtcConnector(Options(answer: TimeSpan.FromSeconds(3)));
        var (offerer, offer) = await connector.CreateOfferAsync(Labels, ct);
        await using var _ = offerer;
        var (answerer, _) = await connector.AcceptOfferAsync(offer, ct);
        await using var __ = answerer;

        var error = await Assert.ThrowsAsync<TransportException>(() => answerer.GetChannelAsync("link", ct));
        Assert.Contains("No connection within", error.Message);
        Assert.Equal(TransportState.Failed, answerer.State);
    }

    [Fact]
    public async Task Offering_side_fails_when_the_answering_side_is_gone()
    {
        var ct = Timeout();
        var connector = new WebRtcConnector(Options(connect: TimeSpan.FromSeconds(3)));
        var (offerer, offer) = await connector.CreateOfferAsync(Labels, ct);
        await using var _ = offerer;
        var (answerer, answer) = await connector.AcceptOfferAsync(offer, ct);
        await answerer.DisposeAsync();

        await offerer.ApplyAnswerAsync(answer, ct);

        await Assert.ThrowsAsync<TransportException>(() => offerer.GetChannelAsync("link", ct));
        // Asking again after the end must not hang.
        await Assert.ThrowsAsync<TransportException>(() => offerer.GetChannelAsync("other", ct));
    }

    [Fact]
    public async Task Channels_opened_later_arrive_on_the_other_side_in_both_directions()
    {
        var ct = Timeout();
        var (offerer, answerer) = await ConnectAsync(ct);
        await using var _ = offerer;
        await using var __ = answerer;

        var acceptedByAnswerer = answerer.AcceptChannelsAsync(ct).GetAsyncEnumerator(ct);
        var acceptedByOfferer = offerer.AcceptChannelsAsync(ct).GetAsyncEnumerator(ct);

        var fromOfferer = await offerer.OpenChannelAsync("c:1", ct);
        var incoming = await NextExceptAsync(acceptedByAnswerer, "link");
        Assert.Equal("c:1", incoming.Label);
        await fromOfferer.SendAsync(new byte[] { 42 }, ct);
        await foreach (var message in incoming.ReadAllAsync(ct))
        {
            Assert.Equal(42, message.Span[0]);
            break;
        }

        var fromAnswerer = await answerer.OpenChannelAsync("c:2", ct);
        var incoming2 = await NextExceptAsync(acceptedByOfferer, "link");
        Assert.Equal("c:2", incoming2.Label);
        Assert.True(fromAnswerer.IsOpen);
    }

    [Fact]
    public async Task Closed_channel_closes_on_the_other_side_and_its_label_can_be_reused()
    {
        var ct = Timeout();
        var (offerer, answerer) = await ConnectAsync(ct);
        await using var _ = offerer;
        await using var __ = answerer;
        var accepted = answerer.AcceptChannelsAsync(ct).GetAsyncEnumerator(ct);

        var first = await offerer.OpenChannelAsync("c:job", ct);
        var remote = await NextExceptAsync(accepted, "link");
        await offerer.CloseChannelAsync(first);

        // The reader completes once the close reaches the other side.
        await foreach (var __message in remote.ReadAllAsync(ct))
        {
        }
        Assert.False(remote.IsOpen);

        var second = await offerer.OpenChannelAsync("c:job", ct);
        var remoteAgain = await NextExceptAsync(accepted, "link");
        Assert.NotSame(first, second);
        Assert.Equal("c:job", remoteAgain.Label);
        await second.SendAsync(new byte[] { 7 }, ct);
        await foreach (var message in remoteAgain.ReadAllAsync(ct))
        {
            Assert.Equal(7, message.Span[0]);
            break;
        }
        Assert.Equal(TransportState.Connected, offerer.State);
    }

    [Fact]
    public async Task Many_jobs_share_one_link_in_parallel()
    {
        var ct = Timeout(120);
        var (offerer, answerer) = await ConnectAsync(ct);
        await using var _ = offerer;
        await using var __ = answerer;

        const int jobs = 16;
        const int messages = 20;
        var payloads = Enumerable.Range(0, jobs).Select(_ => RandomNumberGenerator.GetBytes(64 * 1024)).ToArray();

        var receive = Task.Run(async () =>
        {
            var tasks = new List<Task>();
            await foreach (var channel in answerer.AcceptChannelsAsync(ct))
            {
                if (channel.Label == "link")
                    continue;
                var index = int.Parse(channel.Label[2..]);
                tasks.Add(Task.Run(async () =>
                {
                    var count = 0;
                    await foreach (var message in channel.ReadAllAsync(ct))
                    {
                        Assert.True(message.Span.SequenceEqual(payloads[index]), $"job {index} message {count} differs");
                        if (++count == messages)
                            break;
                    }
                    await answerer.CloseChannelAsync(channel);
                }, ct));
                if (tasks.Count == jobs)
                    break;
            }
            await Task.WhenAll(tasks);
        }, ct);

        await Task.WhenAll(Enumerable.Range(0, jobs).Select(i => Task.Run(async () =>
        {
            var channel = await offerer.OpenChannelAsync($"d:{i}", ct);
            for (var m = 0; m < messages; m++)
                await channel.SendAsync(payloads[i], ct);
        }, ct)));
        await receive;

        Assert.Equal(TransportState.Connected, offerer.State);
        Assert.Equal(TransportState.Connected, answerer.State);
    }

    [Fact]
    public async Task Closing_one_side_ends_the_link_on_the_other()
    {
        var ct = Timeout(90);
        var (offerer, answerer) = await ConnectAsync(ct, Options() with { DisconnectGracePeriod = TimeSpan.FromSeconds(2) });
        await using var _ = offerer;
        var accepted = offerer.AcceptChannelsAsync(ct);
        var ended = new TaskCompletionSource<TransportState>(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.StateChanged += (_, state) =>
        {
            if (state is TransportState.Failed or TransportState.Closed)
                ended.TrySetResult(state);
        };

        await answerer.DisposeAsync();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        await foreach (var __channel in accepted)
        {
        }
        await Assert.ThrowsAnyAsync<Exception>(() => offerer.OpenChannelAsync("c:late", ct));
    }

    [Fact]
    public async Task Ice_report_lists_candidates_and_the_selected_pair()
    {
        var ct = Timeout();
        var (offerer, answerer) = await ConnectAsync(ct);
        await using var _ = offerer;
        await using var __ = answerer;

        var report = offerer.GetIceReport();
        Assert.Contains(CandidateType.Host, report.LocalCandidates);
        Assert.Contains(CandidateType.Host, report.RemoteCandidates);
        Assert.False(report.LocalHasPublicAddress);
        Assert.NotNull(report.Selected);
    }

    [Fact]
    public async Task Unreachable_stun_server_does_not_hold_up_the_offer()
    {
        var ct = Timeout();
        // 192.0.2.0/24 is TEST-NET-1: never answers.
        var options = Options() with { IceServers = ["stun:192.0.2.1:3478"], GatheringTimeout = TimeSpan.FromSeconds(2) };
        var started = DateTime.UtcNow;
        var (offerer, offer) = await new WebRtcConnector(options).CreateOfferAsync(Labels, ct);
        await offerer.DisposeAsync();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), $"took {DateTime.UtcNow - started}");
        Assert.Contains(CandidateType.Host, SdpInspector.CandidateTypes(offer.Sdp));
        Assert.DoesNotContain(CandidateType.ServerReflexive, SdpInspector.CandidateTypes(offer.Sdp));
    }

    [Fact]
    public async Task Session_is_bound_to_the_key_that_signed_the_offer()
    {
        var ct = Timeout();
        var key = RandomNumberGenerator.GetBytes(91);
        var connector = new WebRtcConnector(Options());
        var (offerer, offer) = await connector.CreateOfferAsync(Labels, ct);
        await using var _ = offerer;
        var (answerer, _) = await connector.AcceptOfferAsync(offer, key, ct);
        await using var __ = answerer;

        Assert.Equal(key, answerer.RemotePublicKey);
        Assert.Null(offerer.RemotePublicKey);
        answerer.BindRemotePublicKey(key); // same key again is fine
        Assert.Throws<InvalidOperationException>(() => answerer.BindRemotePublicKey(RandomNumberGenerator.GetBytes(91)));
    }

    private static async Task<IMessageChannel> NextExceptAsync(IAsyncEnumerator<IMessageChannel> channels, string skip)
    {
        while (await channels.MoveNextAsync())
        {
            if (channels.Current.Label != skip)
                return channels.Current;
        }
        throw new InvalidOperationException("No more channels.");
    }

    /// <summary>Flips the last hex digit of the DTLS certificate fingerprint.</summary>
    private static string TamperFingerprint(string sdp)
    {
        var tampered = Fingerprint().Replace(sdp, m => m.Groups[1].Value + (m.Groups[2].Value == "0" ? "1" : "0"), 1);
        Assert.NotEqual(sdp, tampered);
        return tampered;
    }

    [GeneratedRegex(@"(a=fingerprint:sha-256 [0-9A-F:]*?)([0-9A-F])(?=\r?\n)")]
    private static partial Regex Fingerprint();
}
