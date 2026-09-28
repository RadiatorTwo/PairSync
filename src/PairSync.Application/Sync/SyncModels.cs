using PairSync.Domain;
using PairSync.Application.Transfers;
using PairSync.Protocol;
using PairSync.SyncEngine;

namespace PairSync.Application.Sync;

public sealed record SyncOptions
{
    /// <summary>Quiet time after the last file system event before a scan.</summary>
    public TimeSpan WatcherSettle { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Full rescan of automatic profiles, in addition to the watcher (plan §8: events can be lost).</summary>
    public TimeSpan RescanInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long tombstones and the trash are kept.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>A round that could not reach the device or finish is tried again after this time.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Index entries per <see cref="IndexUpdate"/> are bounded by this estimated size.</summary>
    public int IndexUpdateBytes { get; init; } = 512 * 1024;

    /// <summary>Largest chunk list sent with an index entry (about 16 GB of file); larger files are fetched without reusing chunks.</summary>
    public int MaxChunkListBytes { get; init; } = 256 * 1024;
}

public enum SyncStatus
{
    UpToDate,
    Scanning,
    Syncing,
    WaitingForDevice,
    Paused,
    OfferPending,
    Declined,
    Detached,
    Problem,

    /// <summary>The other device fetches files from this one.</summary>
    Serving,
}

/// <summary>A profile as the Syncs page shows it.</summary>
public sealed record SyncProfileView(
    SyncProfile Profile,
    string PeerName,
    SyncStatus Status,
    int OpenConflicts,
    int FilesLeft,
    long BytesLeft,
    long BytesDone,
    double BytesPerSecond,
    int FilesSent = 0)
{
    public Guid Id => Profile.Id;

    /// <summary>Files to fetch in this round; <see cref="FilesLeft"/> of them are still open.</summary>
    public int FilesTotal { get; init; }

    /// <summary>Files being fetched or sent right now, largest first.</summary>
    public IReadOnlyList<FileProgress> ActiveFiles { get; init; } = [];
}

/// <summary>What "New profile" asks for.</summary>
public sealed record NewSyncProfile(string Name, Guid DeviceId, string LocalPath, SyncDirection Direction, SyncMode Mode, string Excludes);

/// <summary>A profile another device offered; the user accepts or declines it.</summary>
/// <param name="Direction">As the offering device sees it.</param>
public sealed record IncomingProfileOffer(Guid ProfileId, Guid DeviceId, string DeviceName, string Name, SyncDirection Direction, string Excludes);

/// <summary>What the user chose when accepting an offer.</summary>
public sealed record ProfileAcceptance(
    string LocalPath, SyncDirection Direction, SyncMode Mode, bool AllowRead = true, bool AllowWrite = true, bool AllowDelete = true);

public enum ConflictResolution
{
    KeepThisDevice,
    KeepOtherDevice,
    KeepBoth,
}

public sealed class SyncException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Bytes per second over the last few seconds, from readings taken whenever the status is read. Averaged over a whole
/// round instead, the many small files that come first would hide the rate of the large ones.
/// </summary>
internal sealed class RateMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly Queue<(DateTime At, long Bytes)> _samples = new();

    public void Reset()
    {
        lock (_samples)
            _samples.Clear();
    }

    /// <summary>The rate up to <paramref name="bytes"/> at <paramref name="now"/>; 0 until two readings are half a second apart.</summary>
    public double Read(long bytes, DateTime now)
    {
        lock (_samples)
        {
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 1 && now - _samples.Peek().At > Window)
                _samples.Dequeue();
            var (at, first) = _samples.Peek();
            var seconds = (now - at).TotalSeconds;
            return seconds >= 0.5 ? Math.Max(0, bytes - first) / seconds : 0;
        }
    }
}

internal static class SyncMapping
{
    public static SyncProfileDirection ToWire(this SyncDirection direction) => (SyncProfileDirection)(int)direction;

    public static SyncDirection FromWire(this SyncProfileDirection direction) =>
        Enum.IsDefined(direction) ? (SyncDirection)(int)direction : SyncDirection.TwoWay;

    /// <summary>The direction the other device sees for a profile this device has with <paramref name="direction"/>.</summary>
    public static SyncDirection Mirror(this SyncDirection direction) => direction switch
    {
        SyncDirection.SendOnly => SyncDirection.ReceiveOnly,
        SyncDirection.ReceiveOnly => SyncDirection.SendOnly,
        _ => SyncDirection.TwoWay,
    };

    public static IndexEntry ToEntry(this SyncFile file, int maxChunkListBytes) => new()
    {
        Path = file.Path,
        IsDirectory = file.IsDirectory,
        Size = file.Size,
        Sha256 = file.Sha256,
        Chunks = file.Chunks is { } chunks && chunks.Length <= maxChunkListBytes ? chunks : null,
        MTimeUtc = file.MTimeUtc,
        Version = [.. file.VersionVector.Counters.Select(c => new VersionCounter { DeviceId = c.Key, Counter = c.Value })],
        Deleted = file.Deleted,
        Sequence = file.Sequence,
    };

    /// <summary>An entry from the other device, checked (plan §11); null if it must be ignored.</summary>
    public static SyncFile? FromEntry(IndexEntry entry, long maxFileSize)
    {
        if (entry.Path is not { } path || path != SyncPaths.Normalize(path) || SyncPaths.Check(path) is not null)
            return null;
        if (entry.Size < 0 || entry.Size > maxFileSize || entry.Version is null)
            return null;
        var isFile = !entry.Deleted && !entry.IsDirectory;
        if (isFile && entry.Sha256 is not { Length: 32 })
            return null;
        VersionVector version;
        try
        {
            version = VersionVector.From(entry.Version.Select(c => new KeyValuePair<Guid, long>(c.DeviceId, c.Counter)));
        }
        catch (ArgumentException)
        {
            return null;
        }
        var chunks = isFile && ContentChunks.Parse(entry.Chunks, entry.Size) is not null ? entry.Chunks : null;
        return new SyncFile
        {
            Path = path,
            IsDirectory = entry.IsDirectory && !entry.Deleted,
            Size = isFile ? entry.Size : 0,
            Sha256 = isFile ? entry.Sha256 : null,
            Chunks = chunks,
            MTimeUtc = DateTime.SpecifyKind(entry.MTimeUtc, DateTimeKind.Utc),
            Version = version.ToString(),
            Deleted = entry.Deleted,
            DeletedAtUtc = entry.Deleted ? DateTime.SpecifyKind(entry.MTimeUtc, DateTimeKind.Utc) : null,
            Sequence = entry.Sequence,
        };
    }

    /// <summary>Rough size of an entry in a MessagePack message, to split index updates.</summary>
    public static int EstimatedSize(this IndexEntry entry) =>
        64 + (entry.Path?.Length ?? 0) * 3 + (entry.Sha256?.Length ?? 0) + (entry.Chunks?.Length ?? 0) + (entry.Version?.Length ?? 0) * 28;
}
