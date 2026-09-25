using MessagePack;

namespace PairSync.Protocol;

/// <summary>
/// What device A hands to device B to pair without mDNS (plan §5, steps 1–3): who A is, where it listens, and a
/// single-use nonce. Signed by A's device key, see <see cref="SignedInvitation"/>.
/// </summary>
[MessagePackObject]
public sealed record PairingInvitation
{
    [Key(0)] public Guid DeviceId { get; init; }

    [Key(1)] public string? DeviceName { get; init; }

    /// <summary>DER-encoded SubjectPublicKeyInfo; the TLS certificate of A must carry this key.</summary>
    [Key(2)] public byte[]? PublicKey { get; init; }

    [Key(3)] public ushort ProtocolMajor { get; init; }

    [Key(4)] public ushort ProtocolMinor { get; init; }

    /// <summary>LAN addresses of A as text; Phase 2 adds internet candidates.</summary>
    [Key(5)] public string[]? Addresses { get; init; }

    [Key(6)] public int Port { get; init; }

    [Key(7)] public byte[]? Nonce { get; init; }

    [Key(8)] public DateTime ExpiresAtUtc { get; init; }
}

/// <summary>The serialized <see cref="PairingInvitation"/> and the signature over it.</summary>
[MessagePackObject]
public sealed record SignedInvitation
{
    [Key(0)] public byte[]? Payload { get; init; }

    [Key(1)] public byte[]? Signature { get; init; }
}

/// <summary>
/// Text form of an invitation for the clipboard, <c>.pairsync-invite</c> files and QR codes:
/// <c>PSI1:</c> followed by the MessagePack <see cref="SignedInvitation"/> in base64url.
/// </summary>
public static class InvitationCodec
{
    public const string Prefix = "PSI1:";

    public const string FileExtension = ".pairsync-invite";

    /// <summary>Far more than a real invitation needs; longer input is rejected before decoding.</summary>
    public const int MaxTextLength = 8 * 1024;

    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>Prepended to the payload before signing, so the signature cannot be mistaken for another purpose.</summary>
    public static ReadOnlySpan<byte> SignatureContext => "PairSync invitation v1\0"u8;

    public static byte[] SerializePayload(PairingInvitation invitation) => MessagePackSerializer.Serialize(invitation, Options);

    /// <summary>The bytes that are signed: <see cref="SignatureContext"/> followed by the payload.</summary>
    public static byte[] SignedData(ReadOnlySpan<byte> payload) => [.. SignatureContext, .. payload];

    public static string Encode(byte[] payload, byte[] signature)
    {
        var bytes = MessagePackSerializer.Serialize(new SignedInvitation { Payload = payload, Signature = signature }, Options);
        return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads the text form; whitespace and line breaks anywhere are ignored.</summary>
    /// <exception cref="FormatException">Not an invitation, or damaged.</exception>
    public static (SignedInvitation Envelope, PairingInvitation Invitation) Decode(string text)
    {
        if (text.Length > MaxTextLength)
            throw new FormatException("The text is too long to be an invitation.");
        text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException($"An invitation starts with {Prefix}");

        var base64 = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            var envelope = MessagePackSerializer.Deserialize<SignedInvitation>(Convert.FromBase64String(base64), Options);
            if (envelope.Payload is null || envelope.Signature is null)
                throw new FormatException("The invitation is incomplete.");
            return (envelope, MessagePackSerializer.Deserialize<PairingInvitation>(envelope.Payload, Options));
        }
        catch (MessagePackSerializationException e)
        {
            throw new FormatException("The invitation is damaged.", e);
        }
    }
}
