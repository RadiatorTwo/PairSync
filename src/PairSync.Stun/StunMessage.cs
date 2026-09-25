using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PairSync.Stun;

/// <summary>The class bits of a STUN message type (RFC 8489 §5).</summary>
public enum StunMessageClass
{
    Request = 0,
    Indication = 1,
    SuccessResponse = 2,
    ErrorResponse = 3,
}

/// <summary>Attribute types this implementation knows (RFC 8489 §18.3, RFC 5389 legacy).</summary>
public static class StunAttributeType
{
    public const ushort MappedAddress = 0x0001;
    public const ushort Username = 0x0006;
    public const ushort MessageIntegrity = 0x0008;
    public const ushort ErrorCode = 0x0009;
    public const ushort UnknownAttributes = 0x000A;
    public const ushort Realm = 0x0014;
    public const ushort Nonce = 0x0015;
    public const ushort MessageIntegritySha256 = 0x001C;
    public const ushort PasswordAlgorithm = 0x001D;
    public const ushort UserHash = 0x001E;
    public const ushort XorMappedAddress = 0x0020;
    public const ushort Software = 0x8022;
    public const ushort Fingerprint = 0x8028;

    /// <summary>
    /// Types below 0x8000 are comprehension-required; a success response carrying one we do not know has to be
    /// treated as a failed transaction (RFC 8489 §6.3.3). Besides the RFC 8489 set, the RFC 3489 address attributes
    /// (0x0002–0x0005) count as known, because old servers still send them and they are harmless to skip.
    /// </summary>
    internal static bool IsKnownComprehensionRequired(ushort type) => type switch
    {
        MappedAddress or 0x0002 or 0x0003 or 0x0004 or 0x0005 or Username or MessageIntegrity or ErrorCode
            or UnknownAttributes or Realm or Nonce or MessageIntegritySha256 or PasswordAlgorithm or UserHash
            or XorMappedAddress => true,
        _ => false,
    };
}

/// <summary>Input that is not a well-formed STUN message.</summary>
public sealed class StunFormatException(string message) : FormatException(message);

/// <summary>
/// A decoded STUN message (RFC 8489), limited to what a Binding client needs: class, method, transaction ID and the
/// mapped address. Parsing treats the input as untrusted: every length is checked, and the only failure is
/// <see cref="TryParse(ReadOnlySpan{byte}, out StunMessage?, out string?)"/> returning <c>false</c> (or
/// <see cref="Parse"/> throwing <see cref="StunFormatException"/>). MESSAGE-INTEGRITY and FINGERPRINT are
/// recognized but not verified; Binding requests to public servers carry no credentials.
/// </summary>
public sealed class StunMessage
{
    public const uint MagicCookie = 0x2112A442;

    public const int HeaderSize = 20;

    public const int TransactionIdSize = 12;

    public const ushort BindingMethod = 0x001;

    private const byte FamilyIpv4 = 0x01;
    private const byte FamilyIpv6 = 0x02;

    private readonly byte[] _transactionId;

    private StunMessage(StunMessageClass messageClass, ushort method, byte[] transactionId)
    {
        Class = messageClass;
        Method = method;
        _transactionId = transactionId;
    }

    public StunMessageClass Class { get; }

    /// <summary>The 12-bit method, <see cref="BindingMethod"/> for everything this client sends.</summary>
    public ushort Method { get; }

    public ReadOnlySpan<byte> TransactionId => _transactionId;

    public IPEndPoint? XorMappedAddress { get; private set; }

    public IPEndPoint? MappedAddress { get; private set; }

    /// <summary>XOR-MAPPED-ADDRESS if present, otherwise the older MAPPED-ADDRESS.</summary>
    public IPEndPoint? MappedEndPoint => XorMappedAddress ?? MappedAddress;

    /// <summary>ERROR-CODE as class * 100 + number, e.g. 400.</summary>
    public int? ErrorCode { get; private set; }

    public string? ErrorReason { get; private set; }

    public string? Software { get; private set; }

    public bool HasMessageIntegrity { get; private set; }

    public bool HasFingerprint { get; private set; }

    /// <summary>Comprehension-required attribute types (below 0x8000) this implementation does not know.</summary>
    public IReadOnlyList<ushort> UnknownComprehensionRequired { get; private set; } = [];

    /// <summary>A fresh random 96-bit transaction ID.</summary>
    public static byte[] NewTransactionId() => RandomNumberGenerator.GetBytes(TransactionIdSize);

    /// <summary>A Binding request without attributes.</summary>
    public static byte[] CreateBindingRequest(ReadOnlySpan<byte> transactionId)
    {
        var buffer = new byte[HeaderSize];
        WriteHeader(buffer, StunMessageClass.Request, BindingMethod, 0, transactionId);
        return buffer;
    }

    /// <summary>A Binding success response carrying <paramref name="mapped"/> as XOR-MAPPED-ADDRESS or MAPPED-ADDRESS.</summary>
    public static byte[] CreateBindingSuccessResponse(ReadOnlySpan<byte> transactionId, IPEndPoint mapped, bool xor = true)
    {
        var address = Normalize(mapped.Address);
        var addressBytes = address.GetAddressBytes();
        var valueLength = 4 + addressBytes.Length;
        var buffer = new byte[HeaderSize + 4 + valueLength];
        WriteHeader(buffer, StunMessageClass.SuccessResponse, BindingMethod, 4 + valueLength, transactionId);

        var attribute = buffer.AsSpan(HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(attribute, xor ? StunAttributeType.XorMappedAddress : StunAttributeType.MappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(attribute[2..], (ushort)valueLength);
        var value = attribute[4..];
        value[1] = addressBytes.Length == 4 ? FamilyIpv4 : FamilyIpv6;
        BinaryPrimitives.WriteUInt16BigEndian(value[2..], (ushort)mapped.Port);
        addressBytes.CopyTo(value[4..]);
        if (xor)
            ApplyXor(value, buffer.AsSpan(4, 4 + TransactionIdSize));
        return buffer;
    }

    /// <exception cref="StunFormatException">Not a well-formed STUN message.</exception>
    public static StunMessage Parse(ReadOnlySpan<byte> data) =>
        TryParse(data, out var message, out var error) ? message : throw new StunFormatException(error);

    public static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out StunMessage? message) =>
        TryParse(data, out message, out _);

    /// <summary>Decodes one complete STUN message; <paramref name="data"/> must be exactly one datagram.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> data, [NotNullWhen(true)] out StunMessage? message, [NotNullWhen(false)] out string? error)
    {
        message = null;
        if (data.Length < HeaderSize)
            return Fail("shorter than a STUN header", out error);
        var type = BinaryPrimitives.ReadUInt16BigEndian(data);
        if ((type & 0xC000) != 0)
            return Fail("the first two bits are not zero", out error);
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (length % 4 != 0)
            return Fail("message length is not a multiple of 4", out error);
        if (HeaderSize + length != data.Length)
            return Fail("message length does not match the datagram", out error);
        if (BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != MagicCookie)
            return Fail("magic cookie missing", out error);

        var messageClass = (StunMessageClass)(((type >> 7) & 0x2) | ((type >> 4) & 0x1));
        var method = (ushort)((type & 0x000F) | ((type & 0x00E0) >> 1) | ((type & 0x3E00) >> 2));
        var result = new StunMessage(messageClass, method, data.Slice(8, TransactionIdSize).ToArray());
        var cookieAndId = data.Slice(4, 4 + TransactionIdSize);
        List<ushort>? unknown = null;
        bool afterIntegrity = false, afterFingerprint = false;

        var offset = HeaderSize;
        while (offset < data.Length)
        {
            if (afterFingerprint)
                return Fail("attribute after FINGERPRINT", out error);
            if (data.Length - offset < 4)
                return Fail("truncated attribute header", out error);
            var attributeType = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            int attributeLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var padded = (attributeLength + 3) & ~3;
            if (padded > data.Length - offset - 4)
                return Fail($"attribute 0x{attributeType:X4} exceeds the message", out error);
            var value = data.Slice(offset + 4, attributeLength);
            offset += 4 + padded;

            if (attributeType == StunAttributeType.Fingerprint)
            {
                if (attributeLength != 4)
                    return Fail("FINGERPRINT has the wrong size", out error);
                result.HasFingerprint = true;
                afterFingerprint = true;
                continue;
            }

            // Only MESSAGE-INTEGRITY-SHA256 and FINGERPRINT may follow MESSAGE-INTEGRITY; anything else is ignored.
            if (afterIntegrity && attributeType != StunAttributeType.MessageIntegritySha256)
                continue;

            switch (attributeType)
            {
                case StunAttributeType.XorMappedAddress:
                    if (!TryReadAddress(value, cookieAndId, xor: true, out var xorMapped, out error))
                        return false;
                    result.XorMappedAddress ??= xorMapped;
                    break;
                case StunAttributeType.MappedAddress:
                    if (!TryReadAddress(value, cookieAndId, xor: false, out var mapped, out error))
                        return false;
                    result.MappedAddress ??= mapped;
                    break;
                case StunAttributeType.ErrorCode:
                    if (value.Length < 4)
                        return Fail("ERROR-CODE is too short", out error);
                    var errorClass = value[2] & 0x07;
                    var number = value[3];
                    if (errorClass is < 3 or > 6 || number > 99)
                        return Fail("ERROR-CODE is out of range", out error);
                    if (result.ErrorCode is null)
                    {
                        result.ErrorCode = errorClass * 100 + number;
                        result.ErrorReason = Encoding.UTF8.GetString(value[4..]);
                    }
                    break;
                case StunAttributeType.Software:
                    result.Software ??= Encoding.UTF8.GetString(value);
                    break;
                case StunAttributeType.MessageIntegrity:
                    if (attributeLength != 20)
                        return Fail("MESSAGE-INTEGRITY has the wrong size", out error);
                    result.HasMessageIntegrity = true;
                    afterIntegrity = true;
                    break;
                case StunAttributeType.MessageIntegritySha256:
                    if (attributeLength is < 16 or > 32 || attributeLength % 4 != 0)
                        return Fail("MESSAGE-INTEGRITY-SHA256 has the wrong size", out error);
                    result.HasMessageIntegrity = true;
                    afterIntegrity = true;
                    break;
                default:
                    if (attributeType < 0x8000 && !StunAttributeType.IsKnownComprehensionRequired(attributeType))
                        (unknown ??= []).Add(attributeType);
                    break;
            }
        }

        if (unknown is not null)
            result.UnknownComprehensionRequired = unknown;
        message = result;
        error = null;
        return true;
    }

    private static bool TryReadAddress(
        ReadOnlySpan<byte> value, ReadOnlySpan<byte> cookieAndId, bool xor, out IPEndPoint? endPoint,
        [NotNullWhen(false)] out string? error)
    {
        endPoint = null;
        if (value.Length < 4)
            return Fail("address attribute is too short", out error);
        var size = value[1] switch
        {
            FamilyIpv4 => 4,
            FamilyIpv6 => 16,
            _ => 0,
        };
        if (size == 0)
            return Fail("unknown address family", out error);
        if (value.Length != 4 + size)
            return Fail("address attribute has the wrong size", out error);

        Span<byte> copy = stackalloc byte[4 + size];
        value.CopyTo(copy);
        if (xor)
            ApplyXor(copy, cookieAndId);
        endPoint = new IPEndPoint(new IPAddress(copy[4..]), BinaryPrimitives.ReadUInt16BigEndian(copy[2..]));
        error = null;
        return true;
    }

    /// <summary>XORs port and address of an address attribute value with the cookie (and the transaction ID for IPv6).</summary>
    private static void ApplyXor(Span<byte> value, ReadOnlySpan<byte> cookieAndId)
    {
        value[2] ^= cookieAndId[0];
        value[3] ^= cookieAndId[1];
        var address = value[4..];
        for (var i = 0; i < address.Length; i++)
            address[i] ^= cookieAndId[i];
    }

    private static void WriteHeader(Span<byte> buffer, StunMessageClass messageClass, ushort method, int length, ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != TransactionIdSize)
            throw new ArgumentException($"A transaction ID has {TransactionIdSize} bytes.", nameof(transactionId));
        var c = (int)messageClass;
        var type = (method & 0x000F) | ((method & 0x0070) << 1) | ((method & 0x0F80) << 2) | ((c & 1) << 4) | ((c & 2) << 7);
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)length);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], MagicCookie);
        transactionId.CopyTo(buffer[8..]);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool Fail(string reason, out string error)
    {
        error = reason;
        return false;
    }
}
