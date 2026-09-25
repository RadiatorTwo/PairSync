using System.Buffers.Binary;
using PairSync.Protocol;

namespace PairSync.UnitTests;

public sealed class ControlCodecTests
{
    public static TheoryData<IControlMessage> Messages() =>
    [
        new Hello { DeviceName = "laptop-win11", MaxMessageSize = 262144 },
        new HelloAck { DeviceName = "workstation-cachyos", MaxMessageSize = 65536 },
        new TransferPlan
        {
            TransferId = Guid.NewGuid(), FileName = "Photos-2026.tar", FileSize = 98_784_247_808, ChunkSize = ProtocolLimits.ChunkSize,
            ChunkCount = 23_552, LastWriteTimeUtc = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc),
        },
        new TransferPlanAck { TransferId = Guid.NewGuid(), ConfirmedChunks = [0b1011, 0xFF] },
        new ChunkAck { TransferId = Guid.NewGuid(), ChunkIndex = 9780 },
        new ChunkNack { TransferId = Guid.NewGuid(), ChunkIndex = 3, Reason = "hash mismatch" },
        new TransferFinish { TransferId = Guid.NewGuid(), FileSha256 = new byte[32] },
        new TransferResult { TransferId = Guid.NewGuid(), Success = true, Message = "ok" },
        new Pause { TransferId = Guid.NewGuid() },
        new Resume { TransferId = Guid.NewGuid() },
        new Cancel { TransferId = Guid.NewGuid(), Reason = "user" },
        new Ping { Timestamp = 123 },
        new Pong { Timestamp = 456 },
    ];

    [Theory]
    [MemberData(nameof(Messages))]
    public void Every_message_round_trips(IControlMessage message)
    {
        var correlation = Guid.NewGuid();

        var envelope = ControlCodec.Decode(ControlCodec.Encode(message, correlation));

        Assert.Equal(correlation, envelope.CorrelationId);
        Assert.Equal(ProtocolVersion.Minor, envelope.RemoteMinor);
        Assert.Equivalent(message, envelope.Message, strict: true);
    }

    [Fact]
    public void Different_major_version_fails_with_a_readable_message()
    {
        var bytes = ControlCodec.Encode(new Ping(), Guid.NewGuid());
        BinaryPrimitives.WriteUInt16BigEndian(bytes, ProtocolVersion.Major + 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 7);

        var e = Assert.Throws<ProtocolVersionException>(() => ControlCodec.Decode(bytes));

        Assert.Equal(ProtocolVersion.Major + 1, e.RemoteMajor);
        Assert.Contains($"{ProtocolVersion.Major + 1}.7", e.Message);
    }

    [Fact]
    public void Newer_minor_version_is_accepted()
    {
        var bytes = ControlCodec.Encode(new Ping { Timestamp = 5 }, Guid.NewGuid());
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), ProtocolVersion.Minor + 3);

        var envelope = ControlCodec.Decode(bytes);

        Assert.Equal(ProtocolVersion.Minor + 3, envelope.RemoteMinor);
        Assert.Equal(5, Assert.IsType<Ping>(envelope.Message).Timestamp);
    }

    [Fact]
    public void Unknown_message_type_is_reported_not_thrown()
    {
        var bytes = ControlCodec.Encode(new Ping(), Guid.NewGuid());
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 999);

        Assert.Equal(999, Assert.IsType<UnknownControlMessage>(ControlCodec.Decode(bytes).Message).TypeCode);
    }

    [Fact]
    public void Unknown_trailing_fields_are_ignored()
    {
        // A newer peer appended a field with key 2 to Ping: [timestamp, extra, extra2].
        var header = ControlCodec.Encode(new Ping(), Guid.NewGuid())[..ControlCodec.HeaderSize];
        byte[] body = [0x93, 0x2A, 0xA3, (byte)'n', (byte)'e', (byte)'w', 0xC3];

        var envelope = ControlCodec.Decode(header.Concat(body).ToArray());

        Assert.Equal(42, Assert.IsType<Ping>(envelope.Message).Timestamp);
    }

    [Fact]
    public void Truncated_or_oversized_messages_are_rejected()
    {
        Assert.Throws<ProtocolException>(() => ControlCodec.Decode(new byte[ControlCodec.HeaderSize - 1]));
        Assert.Throws<ProtocolException>(() => ControlCodec.Decode(new byte[ProtocolLimits.MaxControlMessageSize + 1]));
    }

    [Fact]
    public void Garbage_body_is_a_protocol_error()
    {
        var bytes = ControlCodec.Encode(new TransferPlan { FileName = "x" }, Guid.NewGuid());
        var garbage = bytes[..ControlCodec.HeaderSize].Concat(new byte[] { 0xC1, 0xC1, 0xC1 }).ToArray();

        Assert.Throws<ProtocolException>(() => ControlCodec.Decode(garbage));
    }
}
