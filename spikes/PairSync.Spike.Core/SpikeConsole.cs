using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using PairSync.SyncEngine;
using PairSync.Transport;

namespace PairSync.Spike;

/// <summary>Console plumbing shared by the two spike programs.</summary>
public static class SpikeConsole
{
    public static readonly string[] ChannelLabels = ["control", "data"];

    public static TransportOptions TransportOptions(string[] stunServers, string? sctpBuffer) => new()
    {
        IceServers = stunServers.Select(NormalizeIceServer).ToArray(),
        SctpBufferSize = sctpBuffer is null ? new TransportOptions().SctpBufferSize : checked((int)ParseSize(sctpBuffer)),
    };

    public static Option<string?> SctpBufferOption() => new("--sctp-buffer")
    {
        Description = "SCTP send/receive buffer, e.g. 1M (library default), 8M (default here), 16M. Caps throughput at buffer / RTT.",
    };

    public static Option<SpikeTransport> TransportOption() => new("--transport")
    {
        Description = "WebRtc (LAN and Internet, ICE/STUN) or Tls (LAN only, direct TCP, much faster). Both sides must match.",
        DefaultValueFactory = _ => SpikeTransport.WebRtc,
    };

    /// <summary>Common console setup for both spike programs.</summary>
    public static void Initialize() => Console.OutputEncoding = System.Text.Encoding.UTF8;

    /// <summary>Accepts "host:port" or a full URI (stun:, stuns:, turn:, turns:).</summary>
    public static string NormalizeIceServer(string server) =>
        server.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) || server.StartsWith("stuns:", StringComparison.OrdinalIgnoreCase) ||
        server.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || server.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)
            ? server
            : $"stun:{server}";

    /// <summary>
    /// STUN failures are silent in ICE: gathering just completes without a public candidate. Seen in
    /// practice when a filtering DNS resolver (Pi-hole, AdGuard) answers stun.l.google.com with 0.0.0.0.
    /// </summary>
    public static void WarnIfNoPublicCandidate(TransportOptions options, SessionDescription local)
    {
        if (options.IceServers.Count > 0 && !local.Sdp.Contains("typ srflx", StringComparison.Ordinal))
            Console.WriteLine("Warning: STUN produced no public (srflx) candidate. Check that the STUN host resolves " +
                              "(filtering DNS resolvers block some) and that outgoing UDP is allowed. Only LAN routes are possible.");
    }

    public static void PrintRoute(ITransportSession session)
    {
        var route = session.Route;
        var kind = route is null ? "unknown" : route.IsRelayed ? "relay" : route.IsDirectLan ? "LAN direct" : "Internet direct";
        Console.WriteLine($"Connected · {kind} · {route?.ToString() ?? "no candidate pair reported"}");
    }

    /// <summary>Prints one status line per second until canceled.</summary>
    public static async Task ReportProgressAsync(TransferStats stats, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long lastBytes = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                stats.SampleMemory();
                var bytes = stats.BytesThisRun;
                var rate = bytes - lastBytes;
                lastBytes = bytes;
                if (stats.ChunkCount == 0)
                    continue;
                var done = stats.ResumedChunks + stats.ChunksConfirmed;
                var remaining = stats.FileSize - Math.Min(stats.FileSize, (long)done * Protocol.ProtocolLimits.ChunkSize);
                var eta = rate > 0 ? TimeSpan.FromSeconds(remaining / (double)rate) : (TimeSpan?)null;
                Console.WriteLine(
                    $"Chunk {done:N0} / {stats.ChunkCount:N0} · {Size(bytes)} this run · {Size(rate)}/s" +
                    $" · {(eta is { } e ? $"{e:hh\\:mm\\:ss} left" : "--")} · RAM {Size(stats.PeakWorkingSet)} peak · retransmits {stats.Retransmits}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Writes a JSON report to ./results for the Phase 0 checklist.</summary>
    public static string WriteReport(string role, SpikeTransport transport, TransferStats stats, ITransportSession? session, TransportOptions options, string outcome)
    {
        stats.SampleMemory();
        var report = new
        {
            role,
            transport = transport.ToString(),
            timestampUtc = DateTime.UtcNow,
            os = Environment.OSVersion.ToString(),
            machine = Environment.MachineName,
            file = stats.FileName,
            fileSize = stats.FileSize,
            chunkCount = stats.ChunkCount,
            resumedChunks = stats.ResumedChunks,
            chunksConfirmedThisRun = stats.ChunksConfirmed,
            bytesThisRun = stats.BytesThisRun,
            transferSeconds = Math.Round(stats.Elapsed.TotalSeconds, 1),
            megabytesPerSecond = Math.Round(stats.BytesPerSecond / 1_000_000, 1),
            mebibytesPerSecond = Math.Round(stats.BytesPerSecond / (1024 * 1024), 1),
            totalSeconds = Math.Round(stats.TotalElapsed.TotalSeconds, 1),
            peakWorkingSetMiB = stats.PeakWorkingSet / (1024 * 1024),
            cpuSeconds = Math.Round(stats.CpuTime.TotalSeconds, 1),
            retransmits = stats.Retransmits,
            gen2Collections = GC.CollectionCount(2),
            gcPauseMs = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds),
            allocatedMiB = GC.GetTotalAllocatedBytes() / (1024 * 1024),
            route = session?.Route?.ToString(),
            iceServers = options.IceServers,
            sctpBufferSize = options.SctpBufferSize,
            outcome,
        };
        Directory.CreateDirectory("results");
        var path = Path.Combine("results", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{role}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static string Size(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return $"{bytes.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>Parses sizes like 512M, 20G, 100GiB or plain byte counts (binary units).</summary>
    public static long ParseSize(string text)
    {
        var value = text.Trim().ToUpperInvariant().Replace("IB", "").Replace("B", "");
        var multiplier = 1L;
        if (value.Length > 0 && char.IsLetter(value[^1]))
        {
            multiplier = value[^1] switch
            {
                'K' => 1L << 10,
                'M' => 1L << 20,
                'G' => 1L << 30,
                'T' => 1L << 40,
                _ => throw new FormatException($"Unknown size unit in '{text}'."),
            };
            value = value[..^1];
        }
        return checked((long)(double.Parse(value, CultureInfo.InvariantCulture) * multiplier));
    }
}
