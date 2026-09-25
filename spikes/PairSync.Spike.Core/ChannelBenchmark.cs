using System.Diagnostics;
using System.Net;
using PairSync.Transport;
using PairSync.Transport.Tls;
using PairSync.Transport.WebRtc;

namespace PairSync.Spike;

/// <summary>Measures raw data channel throughput between two sessions in one process (loopback, no STUN).</summary>
public static class ChannelBenchmark
{
    public static async Task<string> RunAsync(
        SpikeTransport transport, TransportOptions options, long totalBytes, int messageSize, CancellationToken cancellationToken)
    {
        var (offerer, answerer) = await ConnectPairAsync(transport, options, cancellationToken).ConfigureAwait(false);
        await using var _ = offerer.ConfigureAwait(false);
        await using var __ = answerer.ConfigureAwait(false);

        var sender = await offerer.GetChannelAsync("data", cancellationToken).ConfigureAwait(false);
        var receiver = await answerer.GetChannelAsync("data", cancellationToken).ConfigureAwait(false);
        messageSize = Math.Min(messageSize, sender.MaxMessageSize);
        var message = new byte[messageSize];
        Random.Shared.NextBytes(message);
        var count = (int)(totalBytes / messageSize);

        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var receive = Task.Run(async () =>
        {
            var received = 0;
            await foreach (var _ in receiver.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (++received == count)
                    return;
            }
        }, cancellationToken);

        for (var i = 0; i < count; i++)
            await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await receive.ConfigureAwait(false);
        clock.Stop();

        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        var bytes = (long)count * messageSize;
        return $"{transport} · {SpikeConsole.Size(bytes)} in {clock.Elapsed.TotalSeconds:0.0} s = {SpikeConsole.Size(bytes / clock.Elapsed.TotalSeconds)}/s " +
               $"· message {SpikeConsole.Size(messageSize)} · SCTP buffer {SpikeConsole.Size(options.SctpBufferSize)} " +
               $"· CPU {cpu.TotalSeconds:0.0} s ({cpu.TotalSeconds / clock.Elapsed.TotalSeconds:0.0} cores, both ends)";
    }

    /// <summary>Two connected sessions in this process over loopback.</summary>
    public static async Task<(ITransportSession Sender, ITransportSession Receiver)> ConnectPairAsync(
        SpikeTransport transport, TransportOptions options, CancellationToken cancellationToken)
    {
        if (transport == SpikeTransport.Tls)
        {
            using var listener = TlsListener.Start(0, options);
            var accept = listener.AcceptAsync(SpikeConsole.ChannelLabels, cancellationToken);
            var sender = await TlsConnector.ConnectAsync(listener.Endpoint with { Addresses = [IPAddress.Loopback] },
                SpikeConsole.ChannelLabels, options, cancellationToken).ConfigureAwait(false);
            return (sender, await accept.ConfigureAwait(false));
        }

        var connector = new WebRtcConnector(options);
        var (offerer, offer) = await connector.CreateOfferAsync(SpikeConsole.ChannelLabels, cancellationToken).ConfigureAwait(false);
        var (answerer, answer) = await connector.AcceptOfferAsync(offer, cancellationToken).ConfigureAwait(false);
        await offerer.ApplyAnswerAsync(answer, cancellationToken).ConfigureAwait(false);
        return (offerer, answerer);
    }
}
