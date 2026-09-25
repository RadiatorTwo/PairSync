using System.Diagnostics;

namespace PairSync.SyncEngine;

/// <summary>Live counters for one run of a sender or receiver; read by the progress display and the report.</summary>
public sealed class TransferStats
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _transfer = new();
    private TimeSpan _cpuAtStart;
    private TimeSpan? _cpuAtEnd;
    private long _bytesThisRun;
    private int _chunksConfirmed;
    private int _retransmits;
    private long _peakWorkingSet;

    public string FileName { get; set; } = "";

    public long FileSize { get; set; }

    public int ChunkCount { get; set; }

    /// <summary>Chunks the receiver already had when this run started (resume).</summary>
    public int ResumedChunks { get; set; }

    public long BytesThisRun => Interlocked.Read(ref _bytesThisRun);

    public int ChunksConfirmed => Volatile.Read(ref _chunksConfirmed);

    public int Retransmits => Volatile.Read(ref _retransmits);

    /// <summary>Transfer phase only: from the accepted plan to the last confirmed chunk (no code exchange, no final file check).</summary>
    public TimeSpan Elapsed => _transfer.Elapsed;

    /// <summary>Whole run including connection setup and the final whole-file check.</summary>
    public TimeSpan TotalElapsed => _total.Elapsed;

    public long PeakWorkingSet => Interlocked.Read(ref _peakWorkingSet);

    /// <summary>Process CPU time during the transfer phase.</summary>
    public TimeSpan CpuTime => (_cpuAtEnd ?? Process.GetCurrentProcess().TotalProcessorTime) - _cpuAtStart;

    public void TransferStarted()
    {
        _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
        _transfer.Restart();
    }

    public void TransferEnded()
    {
        if (!_transfer.IsRunning)
            return;
        _transfer.Stop();
        _cpuAtEnd = Process.GetCurrentProcess().TotalProcessorTime;
    }

    public void AddBytes(long bytes) => Interlocked.Add(ref _bytesThisRun, bytes);

    public void ChunkConfirmed() => Interlocked.Increment(ref _chunksConfirmed);

    public void Retransmitted() => Interlocked.Increment(ref _retransmits);

    public void SampleMemory()
    {
        var current = Environment.WorkingSet;
        long seen;
        while (current > (seen = Interlocked.Read(ref _peakWorkingSet)) &&
               Interlocked.CompareExchange(ref _peakWorkingSet, current, seen) != seen)
        {
        }
    }

    /// <summary>Average throughput of this run in bytes per second.</summary>
    public double BytesPerSecond => Elapsed.TotalSeconds > 0 ? BytesThisRun / Elapsed.TotalSeconds : 0;
}
