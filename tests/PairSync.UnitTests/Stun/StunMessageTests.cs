using System.Buffers.Binary;
using System.Net;
using PairSync.Stun;

namespace PairSync.UnitTests.Stun;

public sealed class StunMessageTests
{
    private static readonly byte[] Rfc5769TransactionId = Convert.FromHexString("B7E7A701BC34D686FA87DFAE");

    // RFC 5769 §2.1: sample request with SOFTWARE, PRIORITY, ICE-CONTROLLED, USERNAME, MESSAGE-INTEGRITY, FINGERPRINT.
    private static readonly byte[] Rfc5769Request = Convert.FromHexString(
        "000100582112A442B7E7A701BC34D686FA87DFAE" +
        "80220010" + "5354554E207465737420636C69656E74" +
        "00240004" + "6E0001FF" +
        "80290008" + "932FF9B151263B36" +
        "00060009" + "6576746A3A68367659202020" +
        "00080014" + "9AEAA70CBFD8CB56781EF2B5B2D3F249C1B571A2" +
        "80280004" + "E57A3BCF");

    // RFC 5769 §2.2: sample IPv4 response, mapped address 192.0.2.1:32853.
    private static readonly byte[] Rfc5769Ipv4Response = Convert.FromHexString(
        "0101003C2112A442B7E7A701BC34D686FA87DFAE" +
        "8022000B" + "7465737420766563746F7220" +
        "00200008" + "0001A147E112A643" +
        "00080014" + "2B91F599FD9E90C38C7489F92AF9BA53F06BE7D7" +
        "80280004" + "C07D4C96");

    // RFC 5769 §2.3: sample IPv6 response, mapped address [2001:db8:1234:5678:11:2233:4455:6677]:32853.
    private static readonly byte[] Rfc5769Ipv6Response = Convert.FromHexString(
        "010100482112A442B7E7A701BC34D686FA87DFAE" +
        "8022000B" + "7465737420766563746F7220" +
        "00200014" + "0002A1470113A9FAA5D3F179BC25F4B5BED2B9D9" +
        "00080014" + "A382954E4BE67BF11784C97C8292C275BFE3ED41" +
        "80280004" + "C8FB0B4C");

    [Fact]
    public void Rfc5769_ipv4_response_yields_xor_mapped_address()
    {
        var message = StunMessage.Parse(Rfc5769Ipv4Response);

        Assert.Equal(StunMessageClass.SuccessResponse, message.Class);
        Assert.Equal(StunMessage.BindingMethod, message.Method);
        Assert.Equal(Rfc5769TransactionId, message.TransactionId.ToArray());
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 32853), message.MappedEndPoint);
        Assert.Equal("test vector", message.Software);
        Assert.True(message.HasMessageIntegrity);
        Assert.True(message.HasFingerprint);
        Assert.Empty(message.UnknownComprehensionRequired);
    }

    [Fact]
    public void Rfc5769_ipv6_response_yields_xor_mapped_address()
    {
        var message = StunMessage.Parse(Rfc5769Ipv6Response);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8:1234:5678:11:2233:4455:6677"), 32853), message.XorMappedAddress);
        Assert.True(message.HasFingerprint);
    }

    [Fact]
    public void Rfc5769_request_parses_past_integrity_and_fingerprint()
    {
        var message = StunMessage.Parse(Rfc5769Request);

        Assert.Equal(StunMessageClass.Request, message.Class);
        Assert.Equal(StunMessage.BindingMethod, message.Method);
        Assert.Equal("STUN test client", message.Software);
        Assert.Equal([(ushort)0x0024], message.UnknownComprehensionRequired); // PRIORITY is ICE, not STUN
        Assert.True(message.HasMessageIntegrity);
        Assert.True(message.HasFingerprint);
        Assert.Null(message.MappedEndPoint);
    }

    [Fact]
    public void Binding_request_has_header_only()
    {
        var id = StunMessage.NewTransactionId();
        var request = StunMessage.CreateBindingRequest(id);

        Assert.Equal(StunMessage.HeaderSize, request.Length);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42 }, request[..8]);
        var message = StunMessage.Parse(request);
        Assert.Equal(StunMessageClass.Request, message.Class);
        Assert.Equal(id, message.TransactionId.ToArray());
    }

    [Theory]
    [InlineData("192.0.2.1", 32853, true)]
    [InlineData("203.0.113.200", 1, false)]
    [InlineData("2001:db8::42", 65535, true)]
    [InlineData("2001:db8::42", 3478, false)]
    public void Success_response_round_trips(string address, int port, bool xor)
    {
        var mapped = new IPEndPoint(IPAddress.Parse(address), port);
        var bytes = StunMessage.CreateBindingSuccessResponse(Rfc5769TransactionId, mapped, xor);

        var message = StunMessage.Parse(bytes);

        Assert.Equal(StunMessageClass.SuccessResponse, message.Class);
        Assert.Equal(mapped, message.MappedEndPoint);
        Assert.Equal(mapped, xor ? message.XorMappedAddress : message.MappedAddress);
        Assert.Null(xor ? message.MappedAddress : message.XorMappedAddress);
    }

    [Fact]
    public void Xor_mapped_address_wins_over_mapped_address()
    {
        var bytes = Build(0x0101,
            Attribute(StunAttributeType.MappedAddress, "0001" + "0050" + "0A000001"),
            XorIpv4Attribute());

        var message = StunMessage.Parse(bytes);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 80), message.MappedAddress);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 32853), message.MappedEndPoint);
    }

    [Fact]
    public void Error_response_carries_code_and_reason()
    {
        var bytes = Build(0x0111, Attribute(StunAttributeType.ErrorCode, "00000401" + Convert.ToHexString("Bad Request"u8)));

        var message = StunMessage.Parse(bytes);

        Assert.Equal(StunMessageClass.ErrorResponse, message.Class);
        Assert.Equal(401, message.ErrorCode);
        Assert.Equal("Bad Request", message.ErrorReason);
    }

    [Fact]
    public void Unknown_optional_attribute_is_ignored_and_unknown_required_is_reported()
    {
        var bytes = Build(0x0101, Attribute(0x8055, "01020304050607"), Attribute(0x7001, "AA"), XorIpv4Attribute());

        var message = StunMessage.Parse(bytes);

        Assert.Equal([(ushort)0x7001], message.UnknownComprehensionRequired);
        Assert.NotNull(message.MappedEndPoint);
    }

    [Fact]
    public void Attributes_after_message_integrity_are_ignored()
    {
        var bytes = Build(0x0101, Attribute(StunAttributeType.MessageIntegrity, new string('0', 40)), XorIpv4Attribute());

        var message = StunMessage.Parse(bytes);

        Assert.True(message.HasMessageIntegrity);
        Assert.Null(message.MappedEndPoint);
    }

    public static TheoryData<string, byte[]> Malformed => new()
    {
        { "empty", [] },
        { "short header", Rfc5769Ipv4Response[..19] },
        { "truncated datagram", Rfc5769Ipv4Response[..^4] },
        { "trailing bytes", [.. Rfc5769Ipv4Response, 0, 0, 0, 0] },
        { "top bits set", Patch(Rfc5769Ipv4Response, 0, 0xC1) },
        { "length not multiple of 4", Build(0x0101, "00000000", declaredLength: 2) },
        { "wrong magic cookie", Patch(Rfc5769Ipv4Response, 4, 0x22) },
        { "attribute longer than message", Build(0x0101, "00200010" + "0001A147E112A643") },
        { "empty address attribute", Build(0x0101, XorIpv4Attribute() + "00200000") },
        { "padding missing", Build(0x0101, "80220005" + "41424344") },
        { "unknown address family", Build(0x0101, Attribute(StunAttributeType.XorMappedAddress, "0003A147E112A643")) },
        { "ipv4 address with ipv6 length", Build(0x0101, Attribute(StunAttributeType.XorMappedAddress, "0001A147" + new string('0', 32))) },
        { "ipv6 address with ipv4 length", Build(0x0101, Attribute(StunAttributeType.XorMappedAddress, "0002A147E112A643")) },
        { "address too short", Build(0x0101, Attribute(StunAttributeType.MappedAddress, "0001")) },
        { "error code out of range", Build(0x0111, Attribute(StunAttributeType.ErrorCode, "00000764")) },
        { "fingerprint wrong size", Build(0x0101, Attribute(StunAttributeType.Fingerprint, "0102")) },
        { "attribute after fingerprint", Build(0x0101, Attribute(StunAttributeType.Fingerprint, "01020304") + XorIpv4Attribute()) },
        { "message integrity wrong size", Build(0x0101, Attribute(StunAttributeType.MessageIntegrity, "01020304")) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_message_is_rejected(string description, byte[] data)
    {
        Assert.False(StunMessage.TryParse(data, out _, out var error), description);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<StunFormatException>(() => StunMessage.Parse(data));
    }

    [Fact]
    public void Random_and_mutated_input_never_throws_anything_else()
    {
        var random = new Random(4711);
        byte[][] seeds = [Rfc5769Request, Rfc5769Ipv4Response, Rfc5769Ipv6Response];
        for (var i = 0; i < 20_000; i++)
        {
            byte[] data;
            if (i % 4 == 0)
            {
                data = new byte[random.Next(0, 120)];
                random.NextBytes(data);
            }
            else
            {
                data = (byte[])seeds[i % seeds.Length].Clone();
                for (var flips = random.Next(1, 4); flips > 0; flips--)
                    data[random.Next(2, data.Length)] = (byte)random.Next(256); // keep the type, hit length and attributes
                if (i % 7 == 0)
                    data = data[..random.Next(0, data.Length)];
            }

            if (StunMessage.TryParse(data, out var message, out var error))
                Assert.NotNull(message);
            else
                Assert.NotNull(error);
        }
    }

    private static string XorIpv4Attribute() => Attribute(StunAttributeType.XorMappedAddress, "0001A147E112A643");

    /// <summary>Type, length and value in hex, padded to 4 bytes with zeros.</summary>
    private static string Attribute(ushort type, string valueHex)
    {
        var length = valueHex.Length / 2;
        return $"{type:X4}{length:X4}{valueHex}{new string('0', ((4 - length % 4) % 4) * 2)}";
    }

    private static byte[] Build(ushort type, string attributesHex = "", int? declaredLength = null)
    {
        var attributes = Convert.FromHexString(attributesHex);
        var data = new byte[StunMessage.HeaderSize + attributes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, type);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)(declaredLength ?? attributes.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), StunMessage.MagicCookie);
        Rfc5769TransactionId.CopyTo(data, 8);
        attributes.CopyTo(data, StunMessage.HeaderSize);
        return data;
    }

    private static byte[] Build(ushort type, params string[] attributes) => Build(type, string.Concat(attributes));

    private static byte[] Patch(byte[] source, int index, byte value)
    {
        var copy = (byte[])source.Clone();
        copy[index] = value;
        return copy;
    }
}
