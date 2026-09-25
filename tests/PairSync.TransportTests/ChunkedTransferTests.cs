using System.Security.Cryptography;
using PairSync.Protocol;
using PairSync.Spike;
using PairSync.SyncEngine;

namespace PairSync.TransportTests;

public sealed class ChunkedTransferTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-transfer-");
    private readonly InMemoryChunkJournal _journal = new();

    public void Dispose() => _root.Delete(recursive: true);

    private static CancellationToken Timeout(int seconds) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    private async Task<FileInfo> CreateSourceAsync(long size)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root.FullName, "source"));
        var file = new FileInfo(Path.Combine(source.FullName, "payload.bin"));
        await TestFileGenerator.GenerateAsync(file.FullName, size, seed: 42, progress: null, TestContext.Current.CancellationToken);
        file.Refresh();
        return file;
    }

    private string TargetDirectory => Directory.CreateDirectory(Path.Combine(_root.FullName, "target")).FullName;

    private static async Task<(PairSync.Protocol.TransferResult Sent, ReceiveOutcome Received)> RunAsync(
        SpikeTransport transport, ChunkedFileSender sender, ChunkedFileReceiver receiver, FileInfo file, CancellationToken ct)
    {
        await using var pair = await LoopbackPair.ConnectAsync(transport, ct);
        var receive = receiver.ReceiveAsync(pair.Answerer, ct);
        var sent = await sender.SendAsync(pair.Offerer, file, ct);
        return (sent, await receive);
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    [Theory]
    [MemberData(nameof(LoopbackPair.Transports), MemberType = typeof(LoopbackPair))]
    public async Task File_with_partial_last_chunk_arrives_intact(SpikeTransport transport)
    {
        var ct = Timeout(120);
        var file = await CreateSourceAsync(64L * 1024 * 1024 + 12_345);
        var sender = new ChunkedFileSender(new SenderOptions());
        var receiver = new ChunkedFileReceiver(TargetDirectory, _journal, new ReceiverOptions());

        var (sent, received) = await RunAsync(transport, sender, receiver, file, ct);

        Assert.True(sent.Success, sent.Message);
        Assert.True(received.Success, received.Message);
        Assert.Equal(Hash(file.FullName), Hash(received.FinalPath!));
        Assert.Equal(17, sender.Stats.ChunkCount);
        Assert.Equal(0, sender.Stats.Retransmits);
        Assert.Empty(Directory.GetFiles(TargetDirectory, "*.pairsync-*"));
    }

    [Theory]
    [MemberData(nameof(LoopbackPair.Transports), MemberType = typeof(LoopbackPair))]
    public async Task Interrupted_transfer_resumes_with_missing_chunks_only(SpikeTransport transport)
    {
        var ct = Timeout(120);
        const int chunks = 12;
        var file = await CreateSourceAsync((long)chunks * ProtocolLimits.ChunkSize);

        // First run: the sender drops the connection after 5 confirmed chunks.
        var firstSender = new ChunkedFileSender(new SenderOptions { AbortAfterChunks = 5 });
        var firstReceiver = new ChunkedFileReceiver(TargetDirectory, _journal, new ReceiverOptions());
        await using (var pair = await LoopbackPair.ConnectAsync(transport, ct))
        {
            var receive = firstReceiver.ReceiveAsync(pair.Answerer, ct);
            await Assert.ThrowsAsync<SimulatedDisconnectException>(() => firstSender.SendAsync(pair.Offerer, file, ct));
            await pair.DisposeAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => receive);
        }

        var transferId = ChunkedFileSender.TransferIdFor(file);
        var journal = await _journal.LoadAsync(transferId, ct);
        Assert.NotNull(journal);
        var kept = ChunkBitmap.FromBytes(journal.Confirmed, chunks).SetCount;
        Assert.InRange(kept, 5, chunks - 1);

        // Second run: both sides restart; only the missing chunks travel.
        var sender = new ChunkedFileSender(new SenderOptions());
        var receiver = new ChunkedFileReceiver(TargetDirectory, _journal, new ReceiverOptions());
        var (sent, received) = await RunAsync(transport, sender, receiver, file, ct);

        Assert.True(sent.Success, sent.Message);
        Assert.True(received.Success, received.Message);
        Assert.Equal(kept, sender.Stats.ResumedChunks);
        Assert.Equal(kept, receiver.Stats.ResumedChunks);
        Assert.Equal(chunks - kept, sender.Stats.ChunksConfirmed);
        Assert.Equal((long)(chunks - kept) * ProtocolLimits.ChunkSize, sender.Stats.BytesThisRun);
        Assert.Equal(Hash(file.FullName), Hash(received.FinalPath!));
        Assert.Null(await _journal.LoadAsync(transferId, ct));
    }

    [Theory]
    [MemberData(nameof(LoopbackPair.Transports), MemberType = typeof(LoopbackPair))]
    public async Task Corrupted_chunk_is_rejected_and_sent_again(SpikeTransport transport)
    {
        var ct = Timeout(120);
        var file = await CreateSourceAsync(5L * ProtocolLimits.ChunkSize);
        var sender = new ChunkedFileSender(new SenderOptions { CorruptChunkOnce = 2 });
        var receiver = new ChunkedFileReceiver(TargetDirectory, _journal, new ReceiverOptions());

        var (sent, received) = await RunAsync(transport, sender, receiver, file, ct);

        Assert.True(received.Success, received.Message);
        Assert.True(sent.Success, sent.Message);
        Assert.Equal(1, sender.Stats.Retransmits);
        Assert.Equal(1, receiver.Stats.Retransmits);
        Assert.Equal(Hash(file.FullName), Hash(received.FinalPath!));
    }

    [Theory]
    [MemberData(nameof(LoopbackPair.Transports), MemberType = typeof(LoopbackPair))]
    public async Task Existing_target_is_kept_and_new_file_gets_a_numbered_name(SpikeTransport transport)
    {
        var ct = Timeout(60);
        var file = await CreateSourceAsync(1024 * 1024);
        var existing = Path.Combine(TargetDirectory, file.Name);
        await File.WriteAllTextAsync(existing, "previous version", ct);

        var (_, received) = await RunAsync(
            transport, new ChunkedFileSender(new SenderOptions()), new ChunkedFileReceiver(TargetDirectory, _journal, new ReceiverOptions()), file, ct);

        Assert.True(received.Success, received.Message);
        Assert.Equal("previous version", await File.ReadAllTextAsync(existing, ct));
        Assert.Equal(Path.Combine(TargetDirectory, "payload (2).bin"), received.FinalPath);
    }

    [Theory(Explicit = true)]
    [MemberData(nameof(LoopbackPair.Transports), MemberType = typeof(LoopbackPair))]
    public async Task Four_gigabytes_over_loopback(SpikeTransport transport)
    {
        var ct = Timeout(1800);
        var file = await CreateSourceAsync(4L * 1024 * 1024 * 1024);
        var sender = new ChunkedFileSender(new SenderOptions());
        var receiver = new ChunkedFileReceiver(TargetDirectory, _journal, new ReceiverOptions());

        var (sent, received) = await RunAsync(transport, sender, receiver, file, ct);

        Assert.True(sent.Success, sent.Message);
        Assert.True(received.Success, received.Message);
        TestContext.Current.SendDiagnosticMessage(
            $"{SpikeConsole.Size(sender.Stats.BytesPerSecond)}/s, peak {SpikeConsole.Size(sender.Stats.PeakWorkingSet)}");
    }
}
