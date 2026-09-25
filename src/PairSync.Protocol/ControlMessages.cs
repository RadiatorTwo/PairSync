using MessagePack;

namespace PairSync.Protocol;

// Control channel messages (plan §7). Serialized as MessagePack arrays with integer keys:
// new optional fields get new keys at the end, and older peers skip keys they do not know.

/// <summary>Marker for control messages; each has a stable type code in <see cref="ControlCodec"/>.</summary>
public interface IControlMessage;

[MessagePackObject]
public sealed record Hello : IControlMessage
{
    [Key(0)] public string? DeviceName { get; init; }

    /// <summary>Largest data message this side will send or accept.</summary>
    [Key(1)] public int MaxMessageSize { get; init; }
}

[MessagePackObject]
public sealed record HelloAck : IControlMessage
{
    [Key(0)] public string? DeviceName { get; init; }

    [Key(1)] public int MaxMessageSize { get; init; }
}

/// <summary>Announces a file transfer. Sent again after reconnecting to resume it.</summary>
[MessagePackObject]
public sealed record TransferPlan : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }

    [Key(1)] public string? FileName { get; init; }

    [Key(2)] public long FileSize { get; init; }

    [Key(3)] public int ChunkSize { get; init; }

    [Key(4)] public int ChunkCount { get; init; }

    [Key(5)] public DateTime LastWriteTimeUtc { get; init; }
}

/// <summary>The receiver accepts a plan and reports chunks it already holds (bitmap, bit i = chunk i).</summary>
[MessagePackObject]
public sealed record TransferPlanAck : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }

    [Key(1)] public byte[]? ConfirmedChunks { get; init; }
}

[MessagePackObject]
public sealed record ChunkAck : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }

    [Key(1)] public int ChunkIndex { get; init; }
}

/// <summary>The chunk failed verification or assembly and must be sent again.</summary>
[MessagePackObject]
public sealed record ChunkNack : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }

    [Key(1)] public int ChunkIndex { get; init; }

    [Key(2)] public string? Reason { get; init; }
}

/// <summary>All chunks sent; carries the SHA-256 of the whole file for the final check.</summary>
[MessagePackObject]
public sealed record TransferFinish : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }

    [Key(1)] public byte[]? FileSha256 { get; init; }
}

[MessagePackObject]
public sealed record TransferResult : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }

    [Key(1)] public bool Success { get; init; }

    [Key(2)] public string? Message { get; init; }
}

[MessagePackObject]
public sealed record Pause : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }
}

[MessagePackObject]
public sealed record Resume : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }
}

[MessagePackObject]
public sealed record Cancel : IControlMessage
{
    [Key(0)] public Guid TransferId { get; init; }

    [Key(1)] public string? Reason { get; init; }
}

[MessagePackObject]
public sealed record Ping : IControlMessage
{
    [Key(0)] public long Timestamp { get; init; }
}

[MessagePackObject]
public sealed record Pong : IControlMessage
{
    [Key(0)] public long Timestamp { get; init; }
}

/// <summary>A message with a type code this version does not know (sent by a newer minor version).</summary>
public sealed record UnknownControlMessage(ushort TypeCode) : IControlMessage;
