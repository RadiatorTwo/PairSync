using MessagePack;

namespace PairSync.Protocol;

// Sync profiles (phase 3, protocol 0.5). Every exchange is a short session that starts with one of these messages:
// an offer or answer about a profile, a request for index entries, a notice that the index changed, or file requests.

/// <summary>Direction of a profile as the sending device sees it.</summary>
public enum SyncProfileDirection
{
    TwoWay = 0,
    SendOnly = 1,
    ReceiveOnly = 2,
}

/// <summary>A device offers to keep a folder in sync; sent again whenever the device is reachable until answered.</summary>
[MessagePackObject]
public sealed record ProfileOffer : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }

    [Key(1)] public string? Name { get; init; }

    [Key(2)] public SyncProfileDirection Direction { get; init; }

    [Key(3)] public string? Excludes { get; init; }
}

/// <summary>The user accepted the offer; <see cref="Direction"/> is as the accepting device sees it.</summary>
[MessagePackObject]
public sealed record ProfileAccept : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }

    [Key(1)] public SyncProfileDirection Direction { get; init; }
}

[MessagePackObject]
public sealed record ProfileDecline : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }

    [Key(1)] public string? Reason { get; init; }
}

/// <summary>The profile ended on the sending device (or it does not know it); the other device detaches it.</summary>
[MessagePackObject]
public sealed record ProfileRemoved : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }
}

/// <summary>"Send me your index entries after <see cref="SeenSequence"/> of index <see cref="IndexId"/>."</summary>
[MessagePackObject]
public sealed record SyncHello : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }

    /// <summary>The other device's index the requester knows; a different id means "from the start".</summary>
    [Key(1)] public Guid IndexId { get; init; }

    [Key(2)] public long SeenSequence { get; init; }
}

[MessagePackObject]
public sealed record VersionCounter
{
    [Key(0)] public Guid DeviceId { get; init; }

    [Key(1)] public long Counter { get; init; }
}

/// <summary>One path of a sync index, including deleted ones.</summary>
[MessagePackObject]
public sealed record IndexEntry
{
    [Key(0)] public string? Path { get; init; }

    [Key(1)] public bool IsDirectory { get; init; }

    [Key(2)] public long Size { get; init; }

    [Key(3)] public byte[]? Sha256 { get; init; }

    /// <summary>SHA-256 per 4 MiB chunk; left out for very large files, which are then fetched without reusing chunks.</summary>
    [Key(4)] public byte[]? ChunkHashes { get; init; }

    [Key(5)] public DateTime MTimeUtc { get; init; }

    [Key(6)] public VersionCounter[]? Version { get; init; }

    [Key(7)] public bool Deleted { get; init; }

    [Key(8)] public long Sequence { get; init; }
}

/// <summary>Part of the answer to <see cref="SyncHello"/>; parts stay below the control message limit.</summary>
[MessagePackObject]
public sealed record IndexUpdate : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }

    [Key(1)] public Guid IndexId { get; init; }

    [Key(2)] public IndexEntry[]? Entries { get; init; }

    /// <summary>The sender's index is complete up to this sequence once all parts arrived.</summary>
    [Key(3)] public long UpToSequence { get; init; }

    [Key(4)] public bool Last { get; init; }
}

/// <summary>"My index changed" (automatic) or "Sync now" (manual): the receiver fetches the index and syncs.</summary>
[MessagePackObject]
public sealed record SyncRequest : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }
}

/// <summary>Asks for exactly this version of a file; the answer is a <see cref="TransferPlan"/> or <see cref="FileUnavailable"/>.</summary>
[MessagePackObject]
public sealed record FileRequest : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }

    [Key(1)] public string? Path { get; init; }

    [Key(2)] public byte[]? Sha256 { get; init; }
}

[MessagePackObject]
public sealed record FileUnavailable : IControlMessage
{
    [Key(0)] public Guid ProfileId { get; init; }

    [Key(1)] public string? Path { get; init; }

    [Key(2)] public string? Reason { get; init; }
}
