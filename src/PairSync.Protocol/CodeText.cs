using System.IO.Compression;
using System.Text;
using MessagePack;

namespace PairSync.Protocol;

/// <summary>
/// Text form shared by invitations and connection codes: a prefix such as <c>PSC1:</c> followed by a signed
/// MessagePack envelope in base64url without padding. Whitespace anywhere is ignored when reading, so codes
/// survive line-wrapping messengers.
/// </summary>
public static class CodeText
{
    /// <summary>Far more than a real code needs; longer input is rejected before decoding.</summary>
    public const int MaxTextLength = 8 * 1024;

    /// <summary>Upper bound for a decompressed session description.</summary>
    public const int MaxSdpLength = 64 * 1024;

    internal static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public static string Encode(string prefix, byte[] payload, byte[] signature)
    {
        var bytes = MessagePackSerializer.Serialize(new SignedInvitation { Payload = payload, Signature = signature }, Options);
        return prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The prefix of <paramref name="text"/> out of <paramref name="prefixes"/>, or null.</summary>
    public static string? PrefixOf(string text, params ReadOnlySpan<string> prefixes)
    {
        var trimmed = text.AsSpan().TrimStart();
        foreach (var prefix in prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return prefix;
        }
        return null;
    }

    /// <summary>Reads the signed envelope behind one of <paramref name="prefixes"/> (the first one is named in errors).</summary>
    /// <param name="name">What the text should be, for messages: "invitation", "connection code".</param>
    /// <exception cref="FormatException">Wrong prefix, too long, or damaged.</exception>
    public static (string Prefix, SignedInvitation Envelope) Decode(string text, string name, params ReadOnlySpan<string> prefixes)
    {
        var article = "aeiou".Contains(name[0]) ? "an" : "a";
        if (text.Length > MaxTextLength)
            throw new FormatException($"The text is too long to be {article} {name}.");
        text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        var prefix = PrefixOf(text, prefixes)
            ?? throw new FormatException($"{char.ToUpperInvariant(article[0])}{article[1..]} {name} starts with {prefixes[0]}");

        var base64 = text[prefix.Length..].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        SignedInvitation envelope;
        try
        {
            envelope = MessagePackSerializer.Deserialize<SignedInvitation>(Convert.FromBase64String(base64), Options);
        }
        catch (Exception e) when (e is MessagePackSerializationException or FormatException)
        {
            throw new FormatException($"The {name} is damaged.", e);
        }
        if (envelope.Payload is null || envelope.Signature is null)
            throw new FormatException($"The {name} is incomplete.");
        return (prefix, envelope);
    }

    public static T DeserializePayload<T>(byte[] payload, string what)
    {
        try
        {
            return MessagePackSerializer.Deserialize<T>(payload, Options);
        }
        catch (MessagePackSerializationException e)
        {
            throw new FormatException($"The {what} is damaged.", e);
        }
    }

    public static byte[] SerializePayload<T>(T payload) => MessagePackSerializer.Serialize(payload, Options);

    /// <summary>The bytes that are signed: a purpose-specific context followed by the payload.</summary>
    public static byte[] SignedData(ReadOnlySpan<byte> context, ReadOnlySpan<byte> payload) => [.. context, .. payload];

    /// <summary>Session descriptions are repetitive text; deflate roughly halves them, which keeps QR codes readable.</summary>
    public static byte[] CompressSdp(string sdp)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize))
            deflate.Write(Encoding.UTF8.GetBytes(sdp));
        return output.ToArray();
    }

    /// <exception cref="FormatException">Not deflate data, or larger than <see cref="MaxSdpLength"/>.</exception>
    public static string DecompressSdp(byte[] compressed)
    {
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var buffer = new byte[MaxSdpLength + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = deflate.Read(buffer, length, buffer.Length - length)) > 0)
                length += read;
            if (length > MaxSdpLength)
                throw new FormatException("The session description is too large.");
            return Encoding.UTF8.GetString(buffer, 0, length);
        }
        catch (InvalidDataException e)
        {
            throw new FormatException("The session description is damaged.", e);
        }
    }
}
