using System.Buffers.Binary;
using System.Security.Cryptography;
using PairSync.Protocol;

namespace PairSync.UnitTests;

public sealed class DataFrameCodecTests
{
    private static readonly byte[] Hash = SHA256.HashData("chunk"u8);

    [Fact]
    public void First_fragment_round_trips_with_hash()
    {
        var payload = RandomNumberGenerator.GetBytes(1000);
        var buffer = new byte[ProtocolLimits.MaxDataMessageSize];
        var length = DataFrameCodec.Write(buffer, 0xABCDEF0123456789, 42, 0, 3, Hash, payload);

        var frame = DataFrameCodec.Read(buffer.AsMemory(0, length));

        Assert.Equal(0xABCDEF0123456789UL, frame.TransferKey);
        Assert.Equal(42, frame.ChunkIndex);
        Assert.Equal(0, frame.FragmentIndex);
        Assert.Equal(3, frame.FragmentCount);
        Assert.Equal(Hash, frame.ChunkSha256.ToArray());
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void Later_fragments_carry_no_hash()
    {
        var buffer = new byte[64];
        var length = DataFrameCodec.Write(buffer, 1, 0, 2, 3, default, [1, 2, 3]);

        var frame = DataFrameCodec.Read(buffer.AsMemory(0, length));

        Assert.Equal(DataFrameCodec.HeaderSize + 3, length);
        Assert.True(frame.ChunkSha256.IsEmpty);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload.ToArray());
    }

    [Theory]
    [InlineData(256 * 1024)]
    [InlineData(64 * 1024)]
    [InlineData(16 * 1024)]
    public void Four_MiB_chunk_splits_into_messages_within_the_limit(int maxMessageSize)
    {
        var perFragment = DataFrameCodec.PayloadPerFragment(maxMessageSize);
        var fragments = DataFrameCodec.FragmentCount(ProtocolLimits.ChunkSize, perFragment);

        Assert.True(DataFrameCodec.HeaderSize + DataFrameCodec.HashSize + perFragment <= maxMessageSize);
        Assert.True((long)fragments * perFragment >= ProtocolLimits.ChunkSize);
        Assert.True((long)(fragments - 1) * perFragment < ProtocolLimits.ChunkSize);
        if (maxMessageSize == 256 * 1024)
            Assert.Equal(17, fragments);
    }

    [Fact]
    public void Empty_chunk_still_has_one_fragment() =>
        Assert.Equal(1, DataFrameCodec.FragmentCount(0, DataFrameCodec.PayloadPerFragment(256 * 1024)));

    [Fact]
    public void Oversized_message_is_rejected() =>
        Assert.Throws<ProtocolException>(() => DataFrameCodec.Read(new byte[ProtocolLimits.MaxDataMessageSize + 1]));

    [Fact]
    public void Truncated_header_is_rejected() =>
        Assert.Throws<ProtocolException>(() => DataFrameCodec.Read(new byte[DataFrameCodec.HeaderSize - 1]));

    [Fact]
    public void First_fragment_without_hash_is_rejected()
    {
        var buffer = new byte[64];
        var length = DataFrameCodec.Write(buffer, 1, 0, 1, 2, default, []);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0); // pretend it is the first fragment

        Assert.Throws<ProtocolException>(() => DataFrameCodec.Read(buffer.AsMemory(0, length)));
    }

    [Theory]
    [InlineData(0, 0)]  // zero fragments
    [InlineData(3, 3)]  // index beyond count
    public void Invalid_fragment_numbers_are_rejected(ushort index, ushort count)
    {
        var buffer = new byte[64];
        DataFrameCodec.Write(buffer, 1, 0, 1, 2, default, []);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), index);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), count);

        Assert.Throws<ProtocolException>(() => DataFrameCodec.Read(buffer.AsMemory(0, DataFrameCodec.HeaderSize)));
    }

    [Fact]
    public void Unknown_kind_and_version_are_rejected()
    {
        var buffer = new byte[64];
        var length = DataFrameCodec.Write(buffer, 1, 0, 1, 2, default, []);

        buffer[0] = 99;
        Assert.Throws<ProtocolException>(() => DataFrameCodec.Read(buffer.AsMemory(0, length)));
        buffer[0] = DataFrameCodec.KindChunkFragment;
        buffer[1] = 99;
        Assert.Throws<ProtocolException>(() => DataFrameCodec.Read(buffer.AsMemory(0, length)));
    }

    [Fact]
    public void Chunk_index_above_int_range_is_rejected()
    {
        var buffer = new byte[64];
        var length = DataFrameCodec.Write(buffer, 1, 0, 1, 2, default, []);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), uint.MaxValue);

        Assert.Throws<ProtocolException>(() => DataFrameCodec.Read(buffer.AsMemory(0, length)));
    }

    [Fact]
    public void Writer_refuses_fragments_larger_than_one_message()
    {
        var buffer = new byte[ProtocolLimits.MaxDataMessageSize * 2];
        var payload = new byte[ProtocolLimits.MaxDataMessageSize];
        Assert.Throws<ArgumentException>(() => DataFrameCodec.Write(buffer, 1, 0, 0, 1, Hash, payload));
    }

    [Fact]
    public void Transfer_key_is_stable_per_transfer_id()
    {
        var id = Guid.NewGuid();
        Assert.Equal(DataFrameCodec.TransferKey(id), DataFrameCodec.TransferKey(id));
        Assert.NotEqual(DataFrameCodec.TransferKey(id), DataFrameCodec.TransferKey(Guid.NewGuid()));
    }
}
