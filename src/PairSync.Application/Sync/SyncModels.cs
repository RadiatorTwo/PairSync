using PairSync.Domain;
using PairSync.Protocol;

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

    /// <summary>Largest chunk hash list sent with an index entry; larger files are fetched without reusing chunks.</summary>
    public int MaxChunkHashBytes { get; init; } = 256 * 1024;
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
    double BytesPerSecond)
{
    public Guid Id => Profile.Id;
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

    public static IndexEntry ToEntry(this SyncFile file, int maxChunkHashBytes) => new()
    {
        Path = file.Path,
        IsDirectory = file.IsDirectory,
        Size = file.Size,
        Sha256 = file.Sha256,
        ChunkHashes = file.ChunkHashes is { } hashes && hashes.Length <= maxChunkHashBytes ? hashes : null,
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
        var chunks = isFile && entry.ChunkHashes is { } hashes && hashes.Length == FileHashes.ChunkCount(entry.Size) * 32 ? hashes : null;
        return new SyncFile
        {
            Path = path,
            IsDirectory = entry.IsDirectory && !entry.Deleted,
            Size = isFile ? entry.Size : 0,
            Sha256 = isFile ? entry.Sha256 : null,
            ChunkHashes = chunks,
            MTimeUtc = DateTime.SpecifyKind(entry.MTimeUtc, DateTimeKind.Utc),
            Version = version.ToString(),
            Deleted = entry.Deleted,
            DeletedAtUtc = entry.Deleted ? DateTime.SpecifyKind(entry.MTimeUtc, DateTimeKind.Utc) : null,
            Sequence = entry.Sequence,
        };
    }

    /// <summary>Rough size of an entry in a MessagePack message, to split index updates.</summary>
    public static int EstimatedSize(this IndexEntry entry) =>
        64 + (entry.Path?.Length ?? 0) * 3 + (entry.Sha256?.Length ?? 0) + (entry.ChunkHashes?.Length ?? 0) + (entry.Version?.Length ?? 0) * 28;
}
