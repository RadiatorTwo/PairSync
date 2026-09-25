using System.Buffers.Binary;
using MessagePack;

namespace PairSync.Protocol;

/// <summary>A decoded control message with its envelope data.</summary>
public sealed record ControlEnvelope(Guid CorrelationId, ushort RemoteMinor, IControlMessage Message);

/// <summary>
/// Control channel framing: a fixed header that can be read without knowing the message type,
/// followed by the MessagePack body.
/// <code>[u16 major][u16 minor][u16 type][16 B correlation id][body]</code>
/// </summary>
public static class ControlCodec
{
    public const int HeaderSize = 2 + 2 + 2 + 16;

    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private static readonly Dictionary<Type, ushort> Codes = new();
    private static readonly Dictionary<ushort, Func<ReadOnlyMemory<byte>, IControlMessage>> Readers = new();

    static ControlCodec()
    {
        // Type codes are part of the wire format: never renumber, only append.
        Register<Hello>(1);
        Register<HelloAck>(2);
        Register<TransferPlan>(10);
        Register<TransferPlanAck>(11);
        Register<ChunkAck>(12);
        Register<ChunkNack>(13);
        Register<TransferFinish>(14);
        Register<TransferResult>(15);
        Register<Pause>(20);
        Register<Resume>(21);
        Register<Cancel>(22);
        Register<Ping>(30);
        Register<Pong>(31);
        Register<PairCommit>(40);
        Register<PairNonce>(41);
        Register<PairReveal>(42);
        Register<PairConfirm>(43);
    }

    private static void Register<T>(ushort code) where T : IControlMessage
    {
        Codes.Add(typeof(T), code);
        Readers.Add(code, body => MessagePackSerializer.Deserialize<T>(body, Options));
    }

    public static byte[] Encode(IControlMessage message, Guid correlationId)
    {
        if (!Codes.TryGetValue(message.GetType(), out var code))
            throw new ArgumentException($"{message.GetType().Name} is not a registered control message.", nameof(message));

        var body = MessagePackSerializer.Serialize(message.GetType(), message, Options);
        if (HeaderSize + body.Length > ProtocolLimits.MaxControlMessageSize)
            throw new ProtocolException($"{message.GetType().Name} exceeds the control message limit.");

        var buffer = new byte[HeaderSize + body.Length];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, ProtocolVersion.Major);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], ProtocolVersion.Minor);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], code);
        correlationId.TryWriteBytes(span[6..22], bigEndian: true, out _);
        body.CopyTo(span[HeaderSize..]);
        return buffer;
    }

    public static ControlEnvelope Decode(ReadOnlyMemory<byte> message)
    {
        if (message.Length > ProtocolLimits.MaxControlMessageSize)
            throw new ProtocolException($"Control message of {message.Length} bytes exceeds the limit.");
        if (message.Length < HeaderSize)
            throw new ProtocolException("Control message is shorter than its header.");

        var span = message.Span;
        var major = BinaryPrimitives.ReadUInt16BigEndian(span);
        var minor = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        if (major != ProtocolVersion.Major)
            throw new ProtocolVersionException(major, minor);

        var code = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var correlationId = new Guid(span[6..22], bigEndian: true);
        if (!Readers.TryGetValue(code, out var reader))
            return new ControlEnvelope(correlationId, minor, new UnknownControlMessage(code));

        try
        {
            return new ControlEnvelope(correlationId, minor, reader(message[HeaderSize..]));
        }
        catch (MessagePackSerializationException e)
        {
            throw new ProtocolException($"Malformed control message (type {code}): {e.Message}");
        }
    }
}
