namespace PairSync.Protocol;

public static class ProtocolVersion
{
    /// <summary>Incompatible changes bump the major version; peers with different majors refuse to talk.</summary>
    public const ushort Major = 0;

    /// <summary>Compatible additions (new optional fields or message types) bump the minor version.</summary>
    public const ushort Minor = 1;

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

/// <summary>Raised when the peer speaks an incompatible major protocol version.</summary>
public sealed class ProtocolVersionException(ushort remoteMajor, ushort remoteMinor)
    : ProtocolException(
        $"The other device uses protocol version {remoteMajor}.{remoteMinor}, this device uses {ProtocolVersion.Current}. " +
        "Update PairSync on the older device.")
{
    public ushort RemoteMajor { get; } = remoteMajor;

    public ushort RemoteMinor { get; } = remoteMinor;
}
