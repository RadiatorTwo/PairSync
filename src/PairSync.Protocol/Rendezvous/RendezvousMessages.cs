using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PairSync.Protocol.Rendezvous;

/// <summary>
/// Messages between a device and the optional rendezvous service (phase 5 block B), one JSON text frame each.
/// The service only relays; everything it forwards is a signed connection code the receiver checks itself.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RvChallenge), "challenge")]
[JsonDerivedType(typeof(RvHello), "hello")]
[JsonDerivedType(typeof(RvWelcome), "welcome")]
[JsonDerivedType(typeof(RvError), "error")]
[JsonDerivedType(typeof(RvWatch), "watch")]
[JsonDerivedType(typeof(RvPresence), "presence")]
[JsonDerivedType(typeof(RvSend), "send")]
[JsonDerivedType(typeof(RvDeliver), "message")]
[JsonDerivedType(typeof(RvUndeliverable), "undeliverable")]
public abstract record RvMessage;

/// <summary>Service → device right after connecting: sign <see cref="Nonce"/> within the login timeout.</summary>
public sealed record RvChallenge(byte[] Nonce, int Version) : RvMessage;

/// <summary>Device → service: the device key and its signature over the challenge (<see cref="RendezvousAuth"/>).</summary>
public sealed record RvHello(byte[] PublicKey, byte[] Signature, string App) : RvMessage;

/// <summary>A TURN relay handed out by the service, with short-lived credentials.</summary>
public sealed record RvIceServer(string Uri, string Username, string Credential);

/// <summary>Service → device: logged in under <see cref="Address"/>; relays valid until <see cref="IceServersExpireAtUtc"/>.</summary>
public sealed record RvWelcome(string Address, IReadOnlyList<RvIceServer> IceServers, DateTime? IceServersExpireAtUtc) : RvMessage;

/// <summary>Service → device, see <see cref="RvErrorCodes"/>. Login errors close the connection.</summary>
public sealed record RvError(string Code, string Message) : RvMessage;

/// <summary>Device → service: the addresses whose presence this device wants to know. Replaces the previous list.</summary>
public sealed record RvWatch(IReadOnlyList<string> Addresses) : RvMessage;

/// <summary>Service → device: a watched address came online or went offline (also once right after a watch).</summary>
public sealed record RvPresence(string Address, bool Online) : RvMessage;

/// <summary>Device → service: deliver <see cref="Data"/> to <see cref="To"/> if it is connected right now.</summary>
public sealed record RvSend(string To, string Data, long Id) : RvMessage;

/// <summary>Service → device: <see cref="Data"/> from the logged-in device <see cref="From"/>.</summary>
public sealed record RvDeliver(string From, string Data) : RvMessage;

/// <summary>Service → device: the send with this id could not be delivered, the target is not connected.</summary>
public sealed record RvUndeliverable(long Id) : RvMessage;

public static class RvErrorCodes
{
    public const string AuthFailed = "auth-failed";
    public const string RateLimited = "rate-limited";
    public const string TooLarge = "too-large";
    public const string Replaced = "replaced";
    public const string BadRequest = "bad-request";
    public const string Busy = "busy";
}

public static class RendezvousLimits
{
    public const int ProtocolVersion = 1;
    public const int NonceSize = 32;

    /// <summary>Largest <see cref="RvSend.Data"/> in characters; a connection code is about 2 KiB.</summary>
    public const int MaxDataLength = 16 * 1024;

    /// <summary>Largest JSON frame either side accepts.</summary>
    public const int MaxFrameSize = 32 * 1024;

    public const int MaxWatchedAddresses = 256;
}

/// <summary>Login to the rendezvous service with the device key (no account, no password).</summary>
public static class RendezvousAuth
{
    private static readonly byte[] Context = "PairSync rendezvous v1\0"u8.ToArray();

    /// <summary>What the device signs for <paramref name="nonce"/>.</summary>
    public static byte[] SignedData(ReadOnlySpan<byte> nonce) => [.. Context, .. nonce];

    /// <summary>ECDSA P-256 / SHA-256 signature by the key in <paramref name="publicKey"/> (SubjectPublicKeyInfo) over the challenge.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length || key.KeySize != 256)
                return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        return key.VerifyData(SignedData(nonce), signature, HashAlgorithmName.SHA256);
    }

    /// <summary>The address of a device at the service: SHA-256 of its public key, lower-case hex.</summary>
    public static string AddressOf(ReadOnlySpan<byte> publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey));

    public static bool IsAddress(string? text) =>
        text is { Length: 64 } && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}

public static class RvCodec
{
    public static string Serialize(RvMessage message) => JsonSerializer.Serialize(message, RvJsonContext.Default.RvMessage);

    public static byte[] SerializeToUtf8(RvMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, RvJsonContext.Default.RvMessage);

    /// <summary>Null for malformed or unknown messages.</summary>
    public static RvMessage? Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, RvJsonContext.Default.RvMessage);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static RvMessage? Deserialize(string text) => Deserialize(Encoding.UTF8.GetBytes(text));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RvMessage))]
internal sealed partial class RvJsonContext : JsonSerializerContext;
