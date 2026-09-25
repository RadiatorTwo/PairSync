using System.Security.Cryptography;

namespace PairSync.Domain;

/// <summary>SHA-256 over the DER-encoded SubjectPublicKeyInfo of a device key.</summary>
public readonly record struct DeviceFingerprint
{
    public const int Size = 32;

    private readonly byte[]? _bytes;

    private DeviceFingerprint(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static DeviceFingerprint Of(ReadOnlySpan<byte> subjectPublicKeyInfo) => new(SHA256.HashData(subjectPublicKeyInfo));

    /// <summary>All 32 bytes, e.g. <c>4A:9C:17:…:B0:3F</c> with 32 groups.</summary>
    public override string ToString() => Format(Bytes);

    /// <summary>Short form for cards, as in the mockup: <c>SHA256: 4A:9C:17:E2:…:B0:3F</c>.</summary>
    public string ToShortString() =>
        _bytes is null ? "" : $"SHA256: {Format(Bytes[..4])}:…:{Format(Bytes[^2..])}";

    public bool Equals(DeviceFingerprint other) => Bytes.SequenceEqual(other.Bytes);

    public override int GetHashCode() => _bytes is null ? 0 : BitConverter.ToInt32(_bytes, 0);

    private static string Format(ReadOnlySpan<byte> bytes) => string.Join(':', bytes.ToArray().Select(b => b.ToString("X2")));
}
