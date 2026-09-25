namespace PairSync.Protocol;

public static class ProtocolVersion
{
    /// <summary>Incompatible changes bump the major version; peers with different majors refuse to talk.</summary>
    public const ushort Major = 0;

    /// <summary>Compatible additions (new optional fields or message types) bump the minor version.</summary>
    public const ushort Minor = 2;

    public static string Current => $"{Major}.{Minor}";
}

public static class ProtocolLimits
{
    /// <summary>Largest control message accepted (plan §7: every message has a size limit).</summary>
    public const int MaxControlMessageSize = 1024 * 1024;

    /// <summary>Unit for hashing, acknowledgement and resume (plan §8).</summary>
    public const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>Upper bound for one data channel message (SCTP default max message size, plan §3).</summary>
    public const int MaxDataMessageSize = 256 * 1024;
}

public class ProtocolException(string message) : Exception(message);

/// <summary>The peer sent a message its access level does not allow, e.g. a transfer before pairing.</summary>
public sealed class PeerNotAuthorizedException(string message) : ProtocolException(message);

/// <summary>A device refused the connection (blocked, removed, wrong identity).</summary>
public sealed class PeerRejectedException(string reason, bool byOtherDevice)
    : ProtocolException(byOtherDevice ? $"The other device refused the connection: {reason}" : $"Connection refused: {reason}")
{
    public string Reason { get; } = reason;

    /// <summary>True if the other device refused, false if this one did.</summary>
    public bool ByOtherDevice { get; } = byOtherDevice;
}

/// <summary>Raised when the peer speaks an incompatible major protocol version.</summary>
public sealed class ProtocolVersionException(ushort remoteMajor, ushort remoteMinor)
    : ProtocolException(
        $"The other device uses protocol version {remoteMajor}.{remoteMinor}, this device uses {ProtocolVersion.Current}. " +
        "Update PairSync on the older device.")
{
    public ushort RemoteMajor { get; } = remoteMajor;

    public ushort RemoteMinor { get; } = remoteMinor;
}
