using PairSync.Domain;
using PairSync.Protocol;
using PairSync.SyncEngine;

namespace PairSync.Application.Transfers;

public sealed record TransferOptions
{
    /// <summary>Received files go to <c>{this}/PairSync/{device}</c> unless the user picks another folder; null = the Downloads folder.</summary>
    public string? DownloadsFolder { get; init; }

    /// <summary>How often waiting jobs are tried again while their device is online.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts per file (changed source, failed verification) before the file counts as failed.</summary>
    public int AttemptsPerFile { get; init; } = 3;

    public SenderOptions Sender { get; init; } = new();

    public ReceiverOptions Receiver { get; init; } = new();
}

/// <summary>A job as the Overview shows it: stored state plus live progress while it runs.</summary>
public sealed record JobView(
    Guid Id,
    TransferDirection Direction,
    Guid PeerDeviceId,
    string PeerName,
    JobState State,
    bool PausedByPeer,
    int FileCount,
    long TotalBytes,
    long TransferredBytes,
    string? CurrentFile,
    int CurrentChunk,
    int CurrentChunkCount,
    double BytesPerSecond,
    int ResumedChunks,
    string? LastError,
    DateTime CreatedAtUtc)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1) : State == JobState.Completed ? 1 : 0;

    public TimeSpan? Remaining => BytesPerSecond > 0 ? TimeSpan.FromSeconds((TotalBytes - TransferredBytes) / BytesPerSecond) : null;
}

/// <summary>Live counters of a running job; the file statistics are replaced for every file.</summary>
internal sealed class JobProgress
{
    private TransferStats? _current;
    private long _currentSize;
    private long _doneBytes;

    public string? CurrentFile { get; private set; }

    public long DoneBytes => Interlocked.Read(ref _doneBytes);

    public void Start(long doneBytes) => Interlocked.Exchange(ref _doneBytes, doneBytes);

    public void FileStarted(string relativePath, long size, TransferStats stats)
    {
        CurrentFile = relativePath;
        _currentSize = size;
        _current = stats;
    }

    public void FileDone(long size)
    {
        Interlocked.Add(ref _doneBytes, size);
        _current = null;
        CurrentFile = null;
    }

    public JobView Apply(JobView view)
    {
        var stats = _current;
        if (stats is null)
            return view with { TransferredBytes = DoneBytes };
        var chunks = stats.ResumedChunks + stats.ChunksConfirmed;
        var inFile = Math.Min(_currentSize, (long)chunks * ProtocolLimits.ChunkSize);
        return view with
        {
            TransferredBytes = DoneBytes + inFile,
            CurrentFile = CurrentFile,
            CurrentChunk = chunks,
            CurrentChunkCount = stats.ChunkCount,
            BytesPerSecond = stats.BytesPerSecond,
            ResumedChunks = stats.ResumedChunks,
        };
    }
}

/// <summary>A top-level entry of an incoming job for the dialog: "Photos/ · 312 files · 1.2 GB".</summary>
public sealed record IncomingEntry(string Name, bool IsFolder, int FileCount, long Size);

/// <summary>
/// An offer from a paired device that waits for the local user (plan: "Empfänger bestätigt Ziel und Umfang, bevor
/// etwas geschrieben wird"). <see cref="AcceptAsync"/> or <see cref="DeclineAsync"/> answers it; if the sender gives
/// up first, <see cref="Closed"/> completes with <see cref="IsWithdrawn"/> set.
/// </summary>
public sealed class IncomingTransfer
{
    private readonly TaskCompletionSource<(bool Accept, string? Folder, ExistingFilePolicy Policy, string? Reason)> _decision =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IncomingTransfer(
        Guid jobId, PairedDevice sender, IReadOnlyList<IncomingEntry> entries, int fileCount, long totalBytes,
        string suggestedFolder, ExistingFilePolicy proposedPolicy, long? freeBytes)
    {
        JobId = jobId;
        PeerDeviceId = sender.Id;
        PeerName = sender.Name;
        Entries = entries;
        FileCount = fileCount;
        TotalBytes = totalBytes;
        SuggestedFolder = suggestedFolder;
        ProposedPolicy = proposedPolicy;
        FreeBytes = freeBytes;
    }

    public Guid JobId { get; }

    public Guid PeerDeviceId { get; }

    public string PeerName { get; }

    public IReadOnlyList<IncomingEntry> Entries { get; }

    public int FileCount { get; }

    public long TotalBytes { get; }

    /// <summary>Default target, <c>Downloads/PairSync/{sender}</c>; the user may choose another folder.</summary>
    public string SuggestedFolder { get; }

    /// <summary>What the sender proposes for existing files; the receiver decides.</summary>
    public ExistingFilePolicy ProposedPolicy { get; }

    /// <summary>Free space at the suggested folder, if known; the dialog warns when it is less than <see cref="TotalBytes"/>.</summary>
    public long? FreeBytes { get; }

    public bool IsWithdrawn { get; private set; }

    /// <summary>Completes once the offer is answered or withdrawn; the dialog closes then.</summary>
    public Task Closed => _closed.Task;

    public Task AcceptAsync(string targetFolder, ExistingFilePolicy policy)
    {
        _decision.TrySetResult((true, Path.GetFullPath(targetFolder), policy, null));
        return Closed;
    }

    public Task DeclineAsync(string reason = "the receiver declined")
    {
        _decision.TrySetResult((false, null, default, reason));
        return Closed;
    }

    internal Task<(bool Accept, string? Folder, ExistingFilePolicy Policy, string? Reason)> Decision => _decision.Task;

    internal void Close(bool withdrawn)
    {
        IsWithdrawn = withdrawn;
        _decision.TrySetResult((false, null, default, "withdrawn"));
        _closed.TrySetResult();
    }
}

internal static class KnownFolders
{
    /// <summary>The user's Downloads folder: <c>XDG_DOWNLOAD_DIR</c> on Linux, otherwise <c>~/Downloads</c>.</summary>
    public static string Downloads()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux() && ReadXdgDownloadDir(home) is { } xdg)
            return xdg;
        return Path.Combine(home, "Downloads");
    }

    private static string? ReadXdgDownloadDir(string home)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        var file = Path.Combine(config, "user-dirs.dirs");
        if (!File.Exists(file))
            return null;
        foreach (var line in File.ReadLines(file))
        {
            if (!line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal))
                continue;
            var value = line["XDG_DOWNLOAD_DIR=".Length..].Trim('"').Replace("$HOME", home, StringComparison.Ordinal);
            return Path.IsPathRooted(value) ? value : null;
        }
        return null;
    }
}

internal static class Policies
{
    public static ExistingFileAction ToWire(this ExistingFilePolicy policy) => (ExistingFileAction)(int)policy;

    public static ExistingFilePolicy FromWire(this ExistingFileAction action) =>
        Enum.IsDefined((ExistingFilePolicy)(int)action) ? (ExistingFilePolicy)(int)action : ExistingFilePolicy.KeepBoth;
}
