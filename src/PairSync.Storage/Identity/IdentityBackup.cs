using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PairSync.Domain;

namespace PairSync.Storage.Identity;

/// <summary>The backup is damaged, not a PairSync identity backup, or the password is wrong.</summary>
public sealed class InvalidIdentityBackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An identity read from a backup file, before it replaces the current one.</summary>
public sealed record RestoredIdentity(Guid DeviceId, byte[] PrivateKey, DeviceFingerprint Fingerprint, DateTime ExportedAtUtc);

/// <summary>
/// Password-protected backup of the device identity (plan phase 6): a <c>.pairsync-identity</c> JSON file with the
/// device id and fingerprint in clear and the private key encrypted with AES-256-GCM under a PBKDF2-SHA256 key.
/// Restoring it on a new installation keeps every pairing valid, because the key stays the same.
/// </summary>
public static class IdentityBackup
{
    public const string FileExtension = ".pairsync-identity";
    public const int MinPasswordLength = 8;
    public const int Iterations = 600_000;
    public const int MaxFileSize = 64 * 1024;

    private const int FormatVersion = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = "PairSync identity backup v1\0"u8.ToArray();

    /// <exception cref="ArgumentException">The password is shorter than <see cref="MinPasswordLength"/>.</exception>
    public static byte[] Create(DeviceIdentity identity, string password, DateTime nowUtc)
    {
        if (password.Length < MinPasswordLength)
            throw new ArgumentException($"The password needs at least {MinPasswordLength} characters.", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, Iterations);
        var plain = identity.ExportPrivateKey();
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag, Bind(identity.Id));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
        var file = new BackupFile
        {
            Format = "pairsync-identity",
            Version = FormatVersion,
            DeviceId = identity.Id,
            Fingerprint = identity.Fingerprint.ToString(),
            ExportedAtUtc = nowUtc,
            Iterations = Iterations,
            Salt = salt,
            Nonce = nonce,
            Ciphertext = cipher,
            Tag = tag,
        };
        return JsonSerializer.SerializeToUtf8Bytes(file, BackupJsonContext.Default.BackupFile);
    }

    /// <summary>Decrypts a backup and checks that the key belongs to the stated device.</summary>
    /// <exception cref="InvalidIdentityBackupException">Damaged, unknown format or wrong password, with a message for the user.</exception>
    public static RestoredIdentity Open(ReadOnlySpan<byte> content, string password)
    {
        if (content.Length > MaxFileSize)
            throw new InvalidIdentityBackupException("This file is too large to be a PairSync identity backup.");
        BackupFile? file;
        try
        {
            file = JsonSerializer.Deserialize(content, BackupJsonContext.Default.BackupFile);
        }
        catch (JsonException e)
        {
            throw new InvalidIdentityBackupException("This file is not a PairSync identity backup.", e);
        }
        if (file is not { Format: "pairsync-identity", Salt.Length: SaltSize, Nonce.Length: NonceSize, Tag.Length: TagSize, Ciphertext.Length: > 0 and < 4096 }
            || file.DeviceId == Guid.Empty)
            throw new InvalidIdentityBackupException("This file is not a PairSync identity backup.");
        if (file.Version != FormatVersion)
            throw new InvalidIdentityBackupException("This identity backup comes from a newer PairSync. Update PairSync first.");
        if (file.Iterations is < 100_000 or > 10_000_000)
            throw new InvalidIdentityBackupException("This identity backup is damaged.");

        var key = DeriveKey(password, file.Salt, file.Iterations);
        var plain = new byte[file.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(file.Nonce, file.Ciphertext, file.Tag, plain, Bind(file.DeviceId));
        }
        catch (AuthenticationTagMismatchException e)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new InvalidIdentityBackupException("Wrong password, or the backup is damaged.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            using var identity = DeviceIdentity.FromPrivateKey(file.DeviceId, plain);
            return new RestoredIdentity(file.DeviceId, plain, identity.Fingerprint, file.ExportedAtUtc);
        }
        catch (CryptographicException e)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new InvalidIdentityBackupException("This identity backup is damaged.", e);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>The device id is authenticated too: a backup cannot be relabeled for another device.</summary>
    private static byte[] Bind(Guid deviceId) => [.. AssociatedData, .. deviceId.ToByteArray()];

    internal sealed record BackupFile
    {
        public string? Format { get; init; }

        public int Version { get; init; }

        public Guid DeviceId { get; init; }

        /// <summary>For people looking at the file; not used when restoring.</summary>
        public string? Fingerprint { get; init; }

        public DateTime ExportedAtUtc { get; init; }

        public int Iterations { get; init; }

        public byte[] Salt { get; init; } = [];

        public byte[] Nonce { get; init; } = [];

        public byte[] Ciphertext { get; init; } = [];

        public byte[] Tag { get; init; } = [];
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IdentityBackup.BackupFile))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
