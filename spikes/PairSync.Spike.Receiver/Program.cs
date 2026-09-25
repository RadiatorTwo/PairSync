using System.CommandLine;
using PairSync.Spike;
using PairSync.SyncEngine;
using PairSync.Transport;

SpikeConsole.Initialize();

// Phase 0 transport spike, receiving side. See docs/spike/phase0-checklist.md.

var directoryArgument = new Argument<DirectoryInfo>("directory") { Description = "Folder to receive into." };
var stunOption = new Option<string[]>("--stun")
{
    Description = "STUN server (host:port or stun:host:port). Repeatable. Without it only LAN host candidates are used.",
    AllowMultipleArgumentsPerToken = false,
};
var sctpBufferOption = SpikeConsole.SctpBufferOption();
var transportOption = SpikeConsole.TransportOption();
var portOption = new Option<int>("--port")
{
    Description = "TCP port to listen on with --transport Tls (0 = any free port).",
    DefaultValueFactory = _ => 47800,
};
var signalDirOption = new Option<DirectoryInfo?>("--signal-dir")
{
    Description = "Exchange offer/answer as files in this folder instead of copy and paste.",
};

var receive = new Command("receive", "Receive one file from PairSync.Spike.Sender.") { directoryArgument, transportOption, portOption, stunOption, signalDirOption, sctpBufferOption };
receive.SetAction(async (parse, cancellationToken) =>
{
    var directory = parse.GetValue(directoryArgument)!;
    directory.Create();

    var options = SpikeConsole.TransportOptions(parse.GetValue(stunOption) ?? [], parse.GetValue(sctpBufferOption));
    var signaling = new ManualSignaling(parse.GetValue(signalDirOption)?.FullName, Console.In, Console.Out);
    var journal = await SpikeJournal.OpenAsync(directory.FullName, cancellationToken);
    var receiver = new ChunkedFileReceiver(directory.FullName, journal, new ReceiverOptions());

    var transport = parse.GetValue(transportOption);
    Console.WriteLine($"Receiving into {directory.FullName} · {transport} · ICE servers: {(options.IceServers.Count == 0 ? "none (LAN only)" : string.Join(", ", options.IceServers))}");
    ITransportSession? session = null;
    using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var progress = SpikeConsole.ReportProgressAsync(receiver.Stats, progressCts.Token);
    var outcome = "failed";
    try
    {
        session = await SpikeConnection.ConnectAsReceiverAsync(transport, parse.GetValue(portOption), options, signaling, cancellationToken);
        SpikeConsole.PrintRoute(session);

        var result = await receiver.ReceiveAsync(session, cancellationToken);
        outcome = result.Success ? "success" : result.Message;
        Console.WriteLine(result.Success ? $"Saved {result.FinalPath}. {result.Message}" : $"Failed: {result.Message}");
        // Give the final TransferResult a moment to leave before the connection is torn down.
        await Task.Delay(500, CancellationToken.None);
        return result.Success ? 0 : 1;
    }
    catch (Exception e) when (e is TransportException or TransferCanceledException or PairSync.Protocol.ProtocolException or OperationCanceledException)
    {
        outcome = e.Message;
        Console.Error.WriteLine($"Stopped: {e.Message}");
        Console.WriteLine($"Progress is kept (*.pairsync-tmp and {SpikeJournal.FileName}). Start both sides again to resume.");
        return 1;
    }
    finally
    {
        await progressCts.CancelAsync();
        await progress;
        if (session is not null)
            await session.DisposeAsync();
        Console.WriteLine($"Report: {SpikeConsole.WriteReport("receiver", transport, receiver.Stats, session, options, outcome)}");
    }
});

var root = new RootCommand("PairSync Phase 0 spike: receiver") { receive };
return await root.Parse(args).InvokeAsync();
