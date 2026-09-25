using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PairSync.Domain;

/// <summary>
/// The short code both users compare when pairing (plan §5, step 4). Commit-reveal keeps either side, or someone in
/// between, from steering it: the connecting device commits to its nonce first, the answering device sends its nonce,
/// then the first one reveals.
/// Code = first 40 bits of <c>SHA-256(sort(keyA, keyB) ‖ nonceCommitter ‖ nonceResponder)</c> as 12 digits.
/// </summary>
public static class SecurityCode
{
    public const int NonceSize = 32;

    public static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public static byte[] Commit(ReadOnlySpan<byte> nonce) => SHA256.HashData(nonce);

    public static bool MatchesCommitment(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> nonce) =>
        commitment.Length == SHA256.HashSizeInBytes && CryptographicOperations.FixedTimeEquals(commitment, Commit(nonce));

    /// <summary>Both devices get the same code: the keys are sorted, the nonces are ordered by role.</summary>
    /// <param name="keyA">SubjectPublicKeyInfo of one device (order does not matter).</param>
    /// <param name="keyB">SubjectPublicKeyInfo of the other device.</param>
    /// <param name="committerNonce">Nonce of the connecting device, the one that committed first.</param>
    /// <param name="responderNonce">Nonce of the answering device.</param>
    /// <returns>12 digits in groups of four, e.g. <c>4827 1930 5561</c>.</returns>
    public static string Derive(ReadOnlySpan<byte> keyA, ReadOnlySpan<byte> keyB, ReadOnlySpan<byte> committerNonce, ReadOnlySpan<byte> responderNonce)
    {
        if (committerNonce.Length != NonceSize || responderNonce.Length != NonceSize)
            throw new ArgumentException($"Pairing nonces have {NonceSize} bytes.");

        var inOrder = keyA.SequenceCompareTo(keyB) <= 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendWithLength(hash, inOrder ? keyA : keyB);
        AppendWithLength(hash, inOrder ? keyB : keyA);
        hash.AppendData(committerNonce);
        hash.AppendData(responderNonce);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(digest);

        // 40 bits cover 0..1.1e12; the modulo folds the small rest onto the first codes, a negligible bias.
        var value = (BinaryPrimitives.ReadUInt64BigEndian(digest) >> 24) % 1_000_000_000_000UL;
        var digits = value.ToString("D12");
        return $"{digits[..4]} {digits[4..8]} {digits[8..]}";
    }

    // Length prefixes keep the key boundary unambiguous; keys of other types or sizes cannot shift bytes across it.
    private static void AppendWithLength(IncrementalHash hash, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        hash.AppendData(length);
        hash.AppendData(data);
    }
}
