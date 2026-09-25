using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using PairSync.Application;
using PairSync.Protocol;
using PairSync.Storage;
using PairSync.SyncEngine;
using PairSync.Transport;
using PairSync.Transport.Tls;

namespace PairSync.IntegrationTests;

/// <summary>The receiver core is shut down mid-transfer and started again; the SQLite journal carries the progress.</summary>
public sealed class ResumeAfterRestartTests : IDisposable
{
    private static readonly string[] Channels = ["control", "data"];
    private static readonly TransportOptions Options = new() { ConnectTimeout = TimeSpan.FromSeconds(20) };

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
        var data = new DataDirectory(Path.Combine(_root.FullName, "receiver-data"));
        var transferId = ChunkedFileSender.TransferIdFor(source);

        int kept;
        await using (var core = await PairSyncCore.StartAsync(data, ct))
        {
            var receiver = new ChunkedFileReceiver(target, core.Services.GetRequiredService<IChunkJournal>(), new ReceiverOptions());
            var sender = new ChunkedFileSender(new SenderOptions { AbortAfterChunks = 5 });
            var (offerer, answerer) = await ConnectAsync(ct);
            var receive = receiver.ReceiveAsync(answerer, ct);
            await Assert.ThrowsAsync<SimulatedDisconnectException>(() => sender.SendAsync(offerer, source, ct));
            await offerer.DisposeAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => receive);
            await answerer.DisposeAsync();

            var entry = await core.Services.GetRequiredService<IChunkJournal>().LoadAsync(transferId, ct);
            Assert.NotNull(entry);
            kept = ChunkBitmap.FromBytes(entry.Confirmed, chunks).SetCount;
            Assert.InRange(kept, 5, chunks - 1);
        }

        await using (var core = await PairSyncCore.StartAsync(data, ct))
        {
            var journal = core.Services.GetRequiredService<IChunkJournal>();
            var receiver = new ChunkedFileReceiver(target, journal, new ReceiverOptions());
            var sender = new ChunkedFileSender(new SenderOptions());
            var (offerer, answerer) = await ConnectAsync(ct);
            await using (offerer)
            await using (answerer)
            {
                var receive = receiver.ReceiveAsync(answerer, ct);
                var sent = await sender.SendAsync(offerer, source, ct);
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

    private static async Task<(ITransportSession Offerer, ITransportSession Answerer)> ConnectAsync(CancellationToken ct)
    {
        using var listener = TlsListener.Start(0, Options);
        var accept = listener.AcceptAsync(Channels, ct);
        var offerer = await TlsConnector.ConnectAsync(listener.Endpoint with { Addresses = [IPAddress.Loopback] }, Channels, Options, ct);
        return (offerer, await accept);
    }
}
