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
    /// <summary>See <see cref="TransferJob.Title"/>.</summary>
    public string Title { get; init; } = "";

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
