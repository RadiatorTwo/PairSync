using System.CommandLine;
using PairSync.Spike;
using PairSync.SyncEngine;
using PairSync.Transport;
using PairSync.Transport.Tls;

SpikeConsole.Initialize();

// Phase 0 transport spike, sending side. See docs/spike/phase0-checklist.md.

var fileArgument = new Argument<FileInfo>("file") { Description = "File to send." };
var stunOption = new Option<string[]>("--stun")
{
    Description = "STUN server (host:port or stun:host:port). Repeatable. Without it only LAN host candidates are used.",
    AllowMultipleArgumentsPerToken = false,
};
var sctpBufferOption = SpikeConsole.SctpBufferOption();
var transportOption = SpikeConsole.TransportOption();
var signalDirOption = new Option<DirectoryInfo?>("--signal-dir")
{
    Description = "Exchange offer/answer as files in this folder instead of copy and paste.",
};
var abortOption = new Option<int?>("--abort-after-chunks")
{
    Description = "Test switch: drop the connection after this many chunks were confirmed in this run.",
};
var windowOption = new Option<int>("--window")
{
    Description = "Unacknowledged 4 MiB chunks in flight.",
    DefaultValueFactory = _ => 16,
};

var send = new Command("send", "Send a file to PairSync.Spike.Receiver.") { fileArgument, transportOption, stunOption, signalDirOption, sctpBufferOption, abortOption, windowOption };
send.SetAction(async (parse, cancellationToken) =>
{
    var file = parse.GetValue(fileArgument)!;
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 2;
    }

    var options = SpikeConsole.TransportOptions(parse.GetValue(stunOption) ?? [], parse.GetValue(sctpBufferOption));
    var signaling = new ManualSignaling(parse.GetValue(signalDirOption)?.FullName, Console.In, Console.Out);
    var sender = new ChunkedFileSender(new SenderOptions
    {
        AbortAfterChunks = parse.GetValue(abortOption),
        Window = parse.GetValue(windowOption),
    });

    var transport = parse.GetValue(transportOption);
    Console.WriteLine($"Sending {file.Name} ({SpikeConsole.Size(file.Length)}) · {transport} · ICE servers: {(options.IceServers.Count == 0 ? "none (LAN only)" : string.Join(", ", options.IceServers))}");
    ITransportSession? session = null;
    using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var progress = SpikeConsole.ReportProgressAsync(sender.Stats, progressCts.Token);
    var outcome = "failed";
    try
    {
        session = await SpikeConnection.ConnectAsSenderAsync(transport, options, signaling, cancellationToken);
        SpikeConsole.PrintRoute(session);

        var channels = await SpikePeer.InitiateAsync(session, cancellationToken);
        var result = await sender.SendAsync(channels, file, cancellationToken);
        outcome = result.Success ? "success" : $"rejected: {result.Message}";
        Console.WriteLine(result.Success ? $"Done. Receiver: {result.Message}" : $"Receiver rejected the file: {result.Message}");
        return result.Success ? 0 : 1;
    }
    catch (SimulatedDisconnectException e)
    {
        outcome = "simulated disconnect";
        Console.WriteLine($"{e.Message} Start both sides again to resume.");
        return 3;
    }
    catch (Exception e) when (e is TransportException or TransferCanceledException or PairSync.Protocol.ProtocolException or OperationCanceledException)
    {
        outcome = e.Message;
        Console.Error.WriteLine($"Stopped: {e.Message}");
        return 1;
    }
    finally
    {
        await progressCts.CancelAsync();
        await progress;
        if (session is not null)
            await session.DisposeAsync();
        Console.WriteLine($"Report: {SpikeConsole.WriteReport("sender", transport, sender.Stats, session, options, outcome)}");
    }
});

var genFile = new Argument<FileInfo>("file") { Description = "File to create." };
var sizeOption = new Option<string>("--size") { Description = "Size, e.g. 2G or 100G.", Required = true };
var seedOption = new Option<ulong>("--seed") { Description = "Seed for the content.", DefaultValueFactory = _ => 1 };
var gen = new Command("gen", "Create an incompressible test file.") { genFile, sizeOption, seedOption };
gen.SetAction(async (parse, cancellationToken) =>
{
    var file = parse.GetValue(genFile)!;
    var size = SpikeConsole.ParseSize(parse.GetValue(sizeOption)!);
    var lastPrint = DateTime.MinValue;
    var progress = new Progress<long>(written =>
    {
        if (DateTime.UtcNow - lastPrint < TimeSpan.FromSeconds(2) && written < size)
            return;
        lastPrint = DateTime.UtcNow;
        Console.WriteLine($"{SpikeConsole.Size(written)} / {SpikeConsole.Size(size)}");
    });
    var free = new DriveInfo(Path.GetPathRoot(file.FullName)!).AvailableFreeSpace + (file.Exists ? file.Length : 0);
    if (free < size)
    {
        Console.Error.WriteLine(
            $"Not enough space: {SpikeConsole.Size(size)} needed, {SpikeConsole.Size(free)} free on {Path.GetPathRoot(file.FullName)}. " +
            "Choose a smaller --size or a path on another drive.");
        return 2;
    }
    try
    {
        await TestFileGenerator.GenerateAsync(file.FullName, size, parse.GetValue(seedOption), progress, cancellationToken);
    }
    catch (IOException e)
    {
        Console.Error.WriteLine($"Could not create the test file: {e.Message}");
        return 1;
    }
    Console.WriteLine($"Created {file.FullName}");
    return 0;
});

var benchSize = new Option<string>("--size") { Description = "Bytes to push through the channel.", DefaultValueFactory = _ => "1G" };
var benchMessage = new Option<string>("--message") { Description = "Message size.", DefaultValueFactory = _ => "256K" };
var benchSctp = SpikeConsole.SctpBufferOption();
var benchTransport = SpikeConsole.TransportOption();
var benchMtu = new Option<int>("--mtu") { Description = "Path MTU for SCTP packets (0 = library default 1280)." };
var bench = new Command("bench", "Raw data channel throughput between two sessions in this process (no files, no hashing).")
{
    benchTransport, benchSize, benchMessage, benchSctp, benchMtu,
};
bench.SetAction(async (parse, cancellationToken) =>
{
    var options = SpikeConsole.TransportOptions([], parse.GetValue(benchSctp)) with { Mtu = parse.GetValue(benchMtu) };
    var total = SpikeConsole.ParseSize(parse.GetValue(benchSize)!);
    var messageSize = (int)SpikeConsole.ParseSize(parse.GetValue(benchMessage)!);
    var result = await ChannelBenchmark.RunAsync(parse.GetValue(benchTransport), options, total, messageSize, cancellationToken);
    Console.WriteLine(result);
    return 0;
});

var natServersOption = new Option<string[]>("--stun")
{
    Description = "STUN servers to compare (at least two for the mapping check). Repeatable.",
    DefaultValueFactory = _ => ["stun:stun.cloudflare.com:3478", "stun:stun.l.google.com:19302", "stun:stun.nextcloud.com:443"],
};
var nat = new Command("nat", "NAT diagnosis on this machine only: can a direct internet connection work from this network?") { natServersOption };
nat.SetAction(async (parse, cancellationToken) =>
{
    var report = await new PairSync.Stun.NatDiagnostics().RunAsync(parse.GetValue(natServersOption)!, cancellationToken);
    foreach (var server in report.Servers)
        Console.WriteLine($"  {server.Server,-38} {server.Status,-12} {server.Ipv4?.MappedAddress?.ToString() ?? server.Detail}");
    Console.WriteLine($"Public address: {report.PublicEndPoint?.ToString() ?? "none"}");
    Console.WriteLine($"Mapping: {report.Mapping} · UDP blocked: {report.UdpBlocked} · DNS filter suspected: {report.DnsFilterSuspected} · CGNAT suspected: {report.CgnatSuspected}");
    Console.WriteLine($"IPv6: global address {report.HasGlobalIpv6Address}, STUN over IPv6 {report.Ipv6StunReachable}");
    Console.WriteLine($"Result: {report.Hint}" + report.Hint switch
    {
        PairSync.Stun.NatHint.Open or PairSync.Stun.NatHint.EndpointIndependent => " (direct connections should work)",
        PairSync.Stun.NatHint.Symmetric => " (symmetric NAT: direct connections only work if the other side has an open NAT)",
        PairSync.Stun.NatHint.UdpBlocked => " (UDP is blocked: no direct internet connection possible)",
        _ => " (not enough answers to tell)",
    });
    return 0;
});

var root = new RootCommand("PairSync Phase 0 spike: sender") { send, gen, bench, nat };
return await root.Parse(args).InvokeAsync();
