using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.SyncEngine;

namespace PairSync.IntegrationTests;

/// <summary>The receiver core is shut down mid-transfer and started again; the SQLite journal carries the progress.</summary>
public sealed class ResumeAfterRestartTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-resume-");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private static CancellationToken Timeout(int seconds) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    [Fact]
    public async Task Transfer_resumes_from_the_sqlite_journal_after_the_receiver_restarts()
    {
        var ct = Timeout(120);
        const int chunks = 12;
        var source = new FileInfo(Path.Combine(_root.FullName, "payload.bin"));
        var content = new byte[chunks * ProtocolLimits.ChunkSize];
        RandomNumberGenerator.Fill(content);
        await File.WriteAllBytesAsync(source.FullName, content, ct);
        var target = Directory.CreateDirectory(Path.Combine(_root.FullName, "target")).FullName;
        var receiverData = new DataDirectory(Path.Combine(_root.FullName, "receiver-data"));
        var transferId = ChunkedFileSender.TransferIdFor(source);
        await using var senderCore = await TestCores.StartAsync(new DataDirectory(Path.Combine(_root.FullName, "sender-data")), ct);

        int kept;
        await using (var core = await TestCores.StartAsync(receiverData, ct))
        {
            await TestCores.PairAsync(senderCore, core, ct);
            var receiver = new ChunkedFileReceiver(target, core.Services.GetRequiredService<IChunkJournal>(), new ReceiverOptions());
            var sender = new ChunkedFileSender(new SenderOptions { AbortAfterChunks = 5 });
            var incoming = TestCores.NextIncomingAsync(core);
            await using (var outgoing = await TestCores.ConnectAsync(senderCore, core, ct))
            await using (var answerer = await incoming)
            {
                var receive = receiver.ReceiveAsync(answerer.Channels, ct);
                await Assert.ThrowsAsync<SimulatedDisconnectException>(() => sender.SendAsync(outgoing.Channels, source, ct));
                await outgoing.DisposeAsync();
                await Assert.ThrowsAnyAsync<Exception>(() => receive);
            }

            var entry = await core.Services.GetRequiredService<IChunkJournal>().LoadAsync(transferId, ct);
            Assert.NotNull(entry);
            kept = ChunkBitmap.FromBytes(entry.Confirmed, chunks).SetCount;
            Assert.InRange(kept, 5, chunks - 1);
        }

        // The receiver restarts (new process, new port); the device list and journal come back from SQLite.
        await using (var core = await TestCores.StartAsync(receiverData, ct))
        {
            var journal = core.Services.GetRequiredService<IChunkJournal>();
            var receiver = new ChunkedFileReceiver(target, journal, new ReceiverOptions());
            var sender = new ChunkedFileSender(new SenderOptions());
            var incoming = TestCores.NextIncomingAsync(core);
            await using (var outgoing = await TestCores.ConnectAsync(senderCore, core, ct))
            await using (var answerer = await incoming)
            {
                var receive = receiver.ReceiveAsync(answerer.Channels, ct);
                var sent = await sender.SendAsync(outgoing.Channels, source, ct);
                var received = await receive;

                Assert.True(sent.Success, sent.Message);
                Assert.True(received.Success, received.Message);
                Assert.Equal(kept, receiver.Stats.ResumedChunks);
                Assert.Equal(chunks - kept, sender.Stats.ChunksConfirmed);
                Assert.Equal(content, await File.ReadAllBytesAsync(received.FinalPath!, ct));
            }
            Assert.Null(await journal.LoadAsync(transferId, ct));
        }
    }
}
