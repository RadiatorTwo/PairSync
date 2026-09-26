namespace PairSync.Domain;

/// <summary>Direction of a sync profile, seen from this device.</summary>
public enum SyncDirection
{
    TwoWay = 0,

    /// <summary>This device only gives: it never takes changes from the other one.</summary>
    SendOnly = 1,

    /// <summary>This device only takes: it never delivers files to the other one.</summary>
    ReceiveOnly = 2,
}

public enum SyncMode
{
    /// <summary>Changes are found by the watcher and a periodic rescan and exchanged right away.</summary>
    Automatic = 0,

    /// <summary>Only "Sync now" on either device exchanges changes.</summary>
    Manual = 1,
}

public enum SyncProfileState
{
    /// <summary>Created here and offered to the other device, which has not answered yet.</summary>
    Offered = 0,

    Active = 1,

    /// <summary>The other device declined the offer.</summary>
    Declined = 2,

    /// <summary>The other device removed the profile (or was removed here); the files stay, nothing is synced.</summary>
    Detached = 3,
}

/// <summary>
/// A folder kept in sync with a folder on one paired device (plan §8, phase 3). Both devices store the profile under
/// the same <see cref="Id"/>, each with its own folder, direction and the rights it grants the other device.
/// </summary>
public sealed class SyncProfile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public Guid PeerDeviceId { get; set; }

    public string LocalPath { get; set; } = "";

    public SyncDirection Direction { get; set; }

    public SyncMode Mode { get; set; }

    public bool Paused { get; set; }

    /// <summary>Exclude patterns, one per line, gitignore-like.</summary>
    public string Excludes { get; set; } = "";

    /// <summary>The other device may fetch files from this folder.</summary>
    public bool AllowRead { get; set; } = true;

    /// <summary>New and changed files from the other device are applied here.</summary>
    public bool AllowWrite { get; set; } = true;

    /// <summary>Deletions on the other device are applied here.</summary>
    public bool AllowDelete { get; set; } = true;

    public SyncProfileState State { get; set; }

    /// <summary>Identifies this device's index; a new id tells the other device to fetch it from the start.</summary>
    public Guid IndexId { get; set; }

    /// <summary>Last sequence number given to a local index entry.</summary>
    public long LastSequence { get; set; }

    /// <summary>Index of the other device the remote entries belong to.</summary>
    public Guid RemoteIndexId { get; set; }

    /// <summary>Entries of the other device's index up to this sequence are known here.</summary>
    public long RemoteSequenceSeen { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastSyncUtc { get; set; }

    /// <summary>Why the profile cannot sync right now (folder missing, other device too old); null if fine.</summary>
    public string? Problem { get; set; }

    /// <summary>Direction and rights together: may changes of the other device be taken at all?</summary>
    public bool TakesChanges => Direction != SyncDirection.SendOnly;

    /// <summary>Direction and rights together: may the other device fetch files here?</summary>
    public bool DeliversFiles => Direction != SyncDirection.ReceiveOnly && AllowRead;
}

public enum SyncSide
{
    /// <summary>What this device's folder holds.</summary>
    Local = 0,

    /// <summary>What the other device last reported for its folder.</summary>
    Remote = 1,
}

/// <summary>An entry of a sync index: one path of a profile on one side, including deleted ones (tombstones).</summary>
public sealed class SyncFile
{
    public Guid ProfileId { get; set; }

    public SyncSide Side { get; set; }

    /// <summary>Relative path with forward slashes, NFC.</summary>
    public string Path { get; set; } = "";

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    /// <summary>SHA-256 of the content; null for directories and tombstones.</summary>
    public byte[]? Sha256 { get; set; }

    /// <summary>SHA-256 of each 4 MiB chunk, concatenated; lets a changed file be updated chunk by chunk.</summary>
    public byte[]? ChunkHashes { get; set; }

    /// <summary>Only a hint for the rescan (skip hashing if size and time are unchanged); never decides equality.</summary>
    public DateTime MTimeUtc { get; set; }

    /// <summary><see cref="VersionVector"/> in its text form.</summary>
    public string Version { get; set; } = "";

    public bool Deleted { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    /// <summary>Order of changes on the side that made them; the other device asks for "everything after".</summary>
    public long Sequence { get; set; }

    public VersionVector VersionVector => VersionVector.Parse(Version);

    /// <summary>Same content: both deleted, both the same directory, or files with the same hash.</summary>
    public bool SameContentAs(SyncFile other) =>
        Deleted == other.Deleted && IsDirectory == other.IsDirectory &&
        (Deleted || IsDirectory || (Size == other.Size && Sha256 is not null && other.Sha256 is not null && Sha256.AsSpan().SequenceEqual(other.Sha256)));
}

/// <summary>A path both devices changed from the same version; both versions were kept.</summary>
public sealed class SyncConflict
{
    public Guid Id { get; set; }

    public Guid ProfileId { get; set; }

    public string Path { get; set; } = "";

    /// <summary>Where the version of the device with the smaller id went.</summary>
    public string CopyPath { get; set; } = "";

    /// <summary>The copy holds this device's version (this device has the smaller id).</summary>
    public bool CopyIsLocal { get; set; }

    public long LocalSize { get; set; }

    public long RemoteSize { get; set; }

    public byte[]? LocalSha256 { get; set; }

    public byte[]? RemoteSha256 { get; set; }

    public DateTime LocalMTimeUtc { get; set; }

    public DateTime RemoteMTimeUtc { get; set; }

    public DateTime DetectedAtUtc { get; set; }

    public bool Resolved { get; set; }
}

public enum SyncActivityKind
{
    /// <summary>A round finished: files taken and deleted.</summary>
    Synced = 0,

    /// <summary>Worth knowing, not an error (a deletion was undone by a change on the other device).</summary>
    Note = 1,

    /// <summary>Something could not be applied (invalid name, no space, locked file).</summary>
    Problem = 2,

    Conflict = 3,
}

/// <summary>What happened in a profile, newest first in the UI; only the latest entries are kept.</summary>
public sealed class SyncActivity
{
    public long Id { get; set; }

    public Guid ProfileId { get; set; }

    public DateTime AtUtc { get; set; }

    public SyncActivityKind Kind { get; set; }

    public string Text { get; set; } = "";

    public int Files { get; set; }

    public long Bytes { get; set; }
}
