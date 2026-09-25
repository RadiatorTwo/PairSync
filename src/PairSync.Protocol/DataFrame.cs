using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PairSync.Protocol;

/// <summary>
/// One fragment of a chunk on the data channel. A 4 MiB chunk (the unit for hashing, acks and
/// resume) is split into fragments that fit a single data channel message (≤ 256 KiB).
/// </summary>
public readonly record struct DataFrame(
    ulong TransferKey,
    int ChunkIndex,
    ushort FragmentIndex,
    ushort FragmentCount,
    ReadOnlyMemory<byte> ChunkSha256,
    ReadOnlyMemory<byte> Payload)
{
    public bool IsFirstFragment => FragmentIndex == 0;
}

/// <summary>
/// Binary layout, big endian, no serializer overhead:
/// <code>[u8 kind=1][u8 version=1][u16 fragIndex][u16 fragCount][u16 reserved][u32 chunkIndex][u64 transferKey]
/// [32 B chunk SHA-256, first fragment only][payload]</code>
/// </summary>
public static class DataFrameCodec
{
    public const byte KindChunkFragment = 1;
    public const byte FormatVersion = 1;
    public const int HeaderSize = 1 + 1 + 2 + 2 + 2 + 4 + 8;
    public const int HashSize = SHA256.HashSizeInBytes;

    /// <summary>Payload bytes per fragment so that every fragment, including the first, fits <paramref name="maxMessageSize"/>.</summary>
    public static int PayloadPerFragment(int maxMessageSize)
    {
        var payload = Math.Min(maxMessageSize, ProtocolLimits.MaxDataMessageSize) - HeaderSize - HashSize;
        return payload > 0 ? payload : throw new ArgumentOutOfRangeException(nameof(maxMessageSize));
    }

    public static int FragmentCount(int chunkLength, int payloadPerFragment) =>
        Math.Max(1, (chunkLength + payloadPerFragment - 1) / payloadPerFragment);

    public static ulong TransferKey(Guid transferId)
    {
        Span<byte> bytes = stackalloc byte[16];
        transferId.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    /// <summary>Writes one fragment into <paramref name="destination"/> and returns the frame length.</summary>
    public static int Write(
        Span<byte> destination, ulong transferKey, int chunkIndex, ushort fragmentIndex, ushort fragmentCount,
        ReadOnlySpan<byte> chunkSha256, ReadOnlySpan<byte> payload)
    {
        var hashLength = fragmentIndex == 0 ? HashSize : 0;
        if (fragmentIndex == 0 && chunkSha256.Length != HashSize)
            throw new ArgumentException("The first fragment carries the chunk hash.", nameof(chunkSha256));
        if (fragmentCount == 0 || fragmentIndex >= fragmentCount)
            throw new ArgumentOutOfRangeException(nameof(fragmentIndex));

        var length = HeaderSize + hashLength + payload.Length;
        if (length > ProtocolLimits.MaxDataMessageSize || length > destination.Length)
            throw new ArgumentException("Fragment does not fit into one data message.", nameof(payload));

        destination[0] = KindChunkFragment;
        destination[1] = FormatVersion;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], fragmentIndex);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], fragmentCount);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], checked((uint)chunkIndex));
        BinaryPrimitives.WriteUInt64BigEndian(destination[12..], transferKey);
        if (hashLength > 0)
            chunkSha256.CopyTo(destination[HeaderSize..]);
        payload.CopyTo(destination[(HeaderSize + hashLength)..]);
        return length;
    }

    /// <summary>Parses and validates a fragment; malformed input throws <see cref="ProtocolException"/>.</summary>
    public static DataFrame Read(ReadOnlyMemory<byte> message)
    {
        var span = message.Span;
        if (message.Length > ProtocolLimits.MaxDataMessageSize)
            throw new ProtocolException($"Data message of {message.Length} bytes exceeds the limit.");
        if (message.Length < HeaderSize)
            throw new ProtocolException("Data message is shorter than its header.");
        if (span[0] != KindChunkFragment)
            throw new ProtocolException($"Unknown data message kind {span[0]}.");
        if (span[1] != FormatVersion)
            throw new ProtocolException($"Unsupported data frame version {span[1]}.");

        var fragmentIndex = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        var fragmentCount = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var chunkIndex = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        var transferKey = BinaryPrimitives.ReadUInt64BigEndian(span[12..]);
        if (fragmentCount == 0 || fragmentIndex >= fragmentCount)
            throw new ProtocolException($"Invalid fragment {fragmentIndex}/{fragmentCount}.");
        if (chunkIndex > int.MaxValue)
            throw new ProtocolException("Chunk index out of range.");

        var hashLength = fragmentIndex == 0 ? HashSize : 0;
        if (message.Length < HeaderSize + hashLength)
            throw new ProtocolException("First fragment is missing the chunk hash.");

        return new DataFrame(
            transferKey,
            (int)chunkIndex,
            fragmentIndex,
            fragmentCount,
            message.Slice(HeaderSize, hashLength),
            message[(HeaderSize + hashLength)..]);
    }
}
