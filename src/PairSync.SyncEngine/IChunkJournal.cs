using System.Collections.Concurrent;
using PairSync.Protocol;

namespace PairSync.SyncEngine;

/// <summary>Receiver-side resume state of one file: which chunks are verified and stored in the temporary file.</summary>
public sealed record ChunkJournalEntry
{
    public Guid TransferId { get; init; }

    public string TempPath { get; init; } = "";

    public long FileSize { get; init; }

    public int ChunkSize { get; init; }

    public int ChunkCount { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    /// <summary><see cref="ChunkBitmap"/> bytes, bit i = chunk i.</summary>
    public byte[] Confirmed { get; init; } = [];

    public static ChunkJournalEntry For(TransferPlan plan, string tempPath, ChunkBitmap confirmed) => new()
    {
        TransferId = plan.TransferId,
        TempPath = tempPath,
        FileSize = plan.FileSize,
        ChunkSize = plan.ChunkSize,
        ChunkCount = plan.ChunkCount,
        LastWriteTimeUtc = plan.LastWriteTimeUtc,
        Confirmed = confirmed.ToBytes(),
    };

    /// <summary>True if the entry describes the same file version at the same place.</summary>
    public bool Matches(TransferPlan plan, string tempPath) =>
        TransferId == plan.TransferId && FileSize == plan.FileSize && ChunkSize == plan.ChunkSize &&
        ChunkCount == plan.ChunkCount && LastWriteTimeUtc == plan.LastWriteTimeUtc &&
        string.Equals(TempPath, tempPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Persists <see cref="ChunkJournalEntry"/> per transfer so a receiver resumes after a disconnect or restart.</summary>
public interface IChunkJournal
{
    Task<ChunkJournalEntry?> LoadAsync(Guid transferId, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces the entry.</summary>
    Task SaveAsync(ChunkJournalEntry entry, CancellationToken cancellationToken);

    Task DeleteAsync(Guid transferId, CancellationToken cancellationToken);
}

/// <summary>Journal that lives as long as the instance; for tests and tools that do not need to survive a restart.</summary>
public sealed class InMemoryChunkJournal : IChunkJournal
{
    private readonly ConcurrentDictionary<Guid, ChunkJournalEntry> _entries = new();

    public Task<ChunkJournalEntry?> LoadAsync(Guid transferId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.GetValueOrDefault(transferId));

    public Task SaveAsync(ChunkJournalEntry entry, CancellationToken cancellationToken)
    {
        _entries[entry.TransferId] = entry;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid transferId, CancellationToken cancellationToken)
    {
        _entries.TryRemove(transferId, out _);
        return Task.CompletedTask;
    }
}
