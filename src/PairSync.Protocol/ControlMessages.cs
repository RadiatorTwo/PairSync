using MessagePack;

namespace PairSync.Protocol;

// Control channel messages (plan §7). Serialized as MessagePack arrays with integer keys:
// new optional fields get new keys at the end, and older peers skip keys they do not know.

/// <summary>Marker for control messages; each has a stable type code in <see cref="ControlCodec"/>.</summary>
public interface IControlMessage;

/// <summary>What the answering device grants after checking the key and id of the connecting one.</summary>
public enum PeerAccess
{
    /// <summary>Paired and not blocked: transfers are possible (each still needs confirmation).</summary>
    Paired = 0,

    /// <summary>Unknown device: only pairing messages are accepted (plan §6).</summary>
    PairingOnly = 1,
}

/// <summary>First message of the connecting device. The device id must belong to the key of its TLS certificate.</summary>
[MessagePackObject]
public sealed record Hello : IControlMessage
{
    [Key(0)] public string? DeviceName { get; init; }

    /// <summary>Largest data message this side will send or accept.</summary>
    [Key(1)] public int MaxMessageSize { get; init; }

    [Key(2)] public Guid DeviceId { get; init; }

    /// <summary>Minor protocol version of the sender (the major version is in every envelope).</summary>
    [Key(3)] public ushort ProtocolMinor { get; init; }

    /// <summary>The session is for pairing: the answering device grants <see cref="PeerAccess.PairingOnly"/> even if it knows the key.</summary>
    [Key(4)] public bool Pairing { get; init; }
}

[MessagePackObject]
public sealed record HelloAck : IControlMessage
{
    [Key(0)] public string? DeviceName { get; init; }

    [Key(1)] public int MaxMessageSize { get; init; }

    [Key(2)] public Guid DeviceId { get; init; }

    [Key(3)] public ushort ProtocolMinor { get; init; }

    [Key(4)] public PeerAccess Access { get; init; }
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

// Pairing (plan §5): commit-reveal for the security code. The connecting device commits to its nonce, the answering
// device replies with its own, then the connecting device reveals. Neither side can steer the code. After both users
// compared the code, each side sends PairConfirm; a Cancel from either side ends the pairing without storing anything.

/// <summary>Connecting device: SHA-256 of its nonce, and the nonce of the invitation it redeems (if any).</summary>
[MessagePackObject]
public sealed record PairCommit : IControlMessage
{
    [Key(0)] public byte[]? Commitment { get; init; }

    /// <summary>Set when pairing with an invitation; the inviting device accepts each nonce only once.</summary>
    [Key(1)] public byte[]? InvitationNonce { get; init; }
}

/// <summary>Answering device: its nonce, sent before it knows the other one.</summary>
[MessagePackObject]
public sealed record PairNonce : IControlMessage
{
    [Key(0)] public byte[]? Nonce { get; init; }
}

/// <summary>Connecting device: the nonce behind <see cref="PairCommit.Commitment"/>.</summary>
[MessagePackObject]
public sealed record PairReveal : IControlMessage
{
    [Key(0)] public byte[]? Nonce { get; init; }
}

/// <summary>The user on the sending side confirmed that both screens show the same code.</summary>
[MessagePackObject]
public sealed record PairConfirm : IControlMessage;

public static class ControlMessageRules
{
    /// <summary>Messages a device that is not paired may send (plan §6: unknown devices get no access).</summary>
    public static bool IsAllowedBeforePairing(IControlMessage message) =>
        message is Hello or HelloAck or Ping or Pong or Cancel or UnknownControlMessage
            or PairCommit or PairNonce or PairReveal or PairConfirm;
}

/// <summary>A message with a type code this version does not know (sent by a newer minor version).</summary>
public sealed record UnknownControlMessage(ushort TypeCode) : IControlMessage;
