using MessagePack;

namespace PairSync.Protocol;

/// <summary>
/// Device A asks a paired device B for an internet connection (plan §6 "Internet ohne Rendezvous", steps 2–3):
/// A's WebRTC offer with all ICE candidates, signed by A's device key. B verifies it against the key stored at
/// pairing, since the offer carries no key of its own.
/// </summary>
[MessagePackObject]
public sealed record ConnectOffer
{
    [Key(0)] public Guid FromDeviceId { get; init; }

    /// <summary>The paired device this code is meant for; any other device refuses it.</summary>
    [Key(1)] public Guid ToDeviceId { get; init; }

    [Key(2)] public ushort ProtocolMajor { get; init; }

    [Key(3)] public ushort ProtocolMinor { get; init; }

    /// <summary>The WebRTC offer, deflated (<see cref="CodeText.CompressSdp"/>).</summary>
    [Key(4)] public byte[]? Sdp { get; init; }

    /// <summary>Single use: B answers each offer once, A accepts one answer per offer.</summary>
    [Key(5)] public byte[]? Nonce { get; init; }

    [Key(6)] public DateTime ExpiresAtUtc { get; init; }

    /// <summary>A's NAT classification (PairSync.Stun.NatHint), for failure messages on B.</summary>
    [Key(7)] public byte NatHint { get; init; }
}

/// <summary>
/// B's reply to a <see cref="ConnectOffer"/> or to an internet invitation (steps 4–5): B's WebRTC answer, bound
/// to the offer by its nonce, signed by B's device key. The key travels along because an invitation's answer comes
/// from a device A does not know yet; for a paired device A compares it with the stored key.
/// </summary>
[MessagePackObject]
public sealed record ConnectAnswer
{
    [Key(0)] public Guid FromDeviceId { get; init; }

    [Key(1)] public string? DeviceName { get; init; }

    /// <summary>DER-encoded SubjectPublicKeyInfo of B's device key.</summary>
    [Key(2)] public byte[]? PublicKey { get; init; }

    [Key(3)] public ushort ProtocolMajor { get; init; }

    [Key(4)] public ushort ProtocolMinor { get; init; }

    /// <summary>Nonce of the offer or invitation this answers.</summary>
    [Key(5)] public byte[]? OfferNonce { get; init; }

    /// <summary>The WebRTC answer, deflated (<see cref="CodeText.CompressSdp"/>).</summary>
    [Key(6)] public byte[]? Sdp { get; init; }

    [Key(7)] public DateTime ExpiresAtUtc { get; init; }

    /// <summary>B's NAT classification (PairSync.Stun.NatHint), for failure messages on A.</summary>
    [Key(8)] public byte NatHint { get; init; }
}

/// <summary>
/// Text forms: <c>PSC1:</c> for <see cref="ConnectOffer"/>, <c>PSR1:</c> for <see cref="ConnectAnswer"/>, each a
/// signed envelope in base64url (<see cref="CodeText"/>). Files use <c>.pairsync-connect</c>.
/// </summary>
public static class ConnectCodec
{
    public const string OfferPrefix = "PSC1:";

    public const string AnswerPrefix = "PSR1:";

    public const string FileExtension = ".pairsync-connect";

    public static ReadOnlySpan<byte> OfferContext => "PairSync connect offer v1\0"u8;

    public static ReadOnlySpan<byte> AnswerContext => "PairSync connect answer v1\0"u8;

    public static bool LooksLikeOffer(string text) => CodeText.PrefixOf(text, OfferPrefix) is not null;

    public static bool LooksLikeAnswer(string text) => CodeText.PrefixOf(text, AnswerPrefix) is not null;

    public static byte[] SerializePayload(ConnectOffer offer) => CodeText.SerializePayload(offer);

    public static byte[] SerializePayload(ConnectAnswer answer) => CodeText.SerializePayload(answer);

    public static string EncodeOffer(byte[] payload, byte[] signature) => CodeText.Encode(OfferPrefix, payload, signature);

    public static string EncodeAnswer(byte[] payload, byte[] signature) => CodeText.Encode(AnswerPrefix, payload, signature);

    /// <exception cref="FormatException">Not a connection code, or damaged.</exception>
    public static (SignedInvitation Envelope, ConnectOffer Offer) DecodeOffer(string text)
    {
        var (_, envelope) = CodeText.Decode(text, "connection code", OfferPrefix);
        return (envelope, CodeText.DeserializePayload<ConnectOffer>(envelope.Payload!, "connection code"));
    }

    /// <exception cref="FormatException">Not an answer code, or damaged.</exception>
    public static (SignedInvitation Envelope, ConnectAnswer Answer) DecodeAnswer(string text)
    {
        var (_, envelope) = CodeText.Decode(text, "answer code", AnswerPrefix);
        return (envelope, CodeText.DeserializePayload<ConnectAnswer>(envelope.Payload!, "answer code"));
    }
}
