using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PairSync.Domain;
using PairSync.Storage.Secrets;

namespace PairSync.Storage.Identity;

/// <summary>The loaded identity and how its private key is protected (Settings warns about <see cref="SecretProtection.File"/>).</summary>
public sealed record LocalIdentity(DeviceIdentity Identity, SecretProtection Protection);

/// <summary>
/// The identity exists but cannot be loaded. PairSync must not silently create a new one: every paired device
/// would stop trusting this one.
/// </summary>
public sealed class IdentityUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Creates the device identity on first start and loads it afterwards. The public part (device id, public key,
/// where the private key is) lives in <c>identity.json</c>; the private key in the secret store as "identity".
/// </summary>
public sealed class DeviceIdentityStore(DataDirectory dataDirectory, ISecretStoreProvider secrets, ILogger<DeviceIdentityStore> logger)
{
    public const string SecretName = "identity";

    public string PublicPath => Path.Combine(dataDirectory.Root, "identity.json");

    public async Task<LocalIdentity> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var record = ReadRecord();
        return record is null
            ? await CreateAsync(cancellationToken).ConfigureAwait(false)
            : await LoadAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalIdentity> CreateAsync(CancellationToken cancellationToken)
    {
        using var store = await secrets.OpenPreferredAsync(cancellationToken).ConfigureAwait(false);
        var identity = DeviceIdentity.Create();
        try
        {
            // Private key first: identity.json must never point to a key that was not saved.
            await store.SaveAsync(SecretName, identity.ExportPrivateKey(), cancellationToken).ConfigureAwait(false);
            WriteRecord(new IdentityRecord
            {
                DeviceId = identity.Id,
                PublicKey = identity.PublicKey,
                Protection = store.Protection,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
        catch
        {
            identity.Dispose();
            throw;
        }

        logger.LogInformation("Created device identity {DeviceId}, fingerprint {Fingerprint}, key protected by {Protection}",
            identity.Id, identity.Fingerprint.ToShortString(), store.Protection);
        if (store.Protection == SecretProtection.File)
            logger.LogWarning("No OS keyring available; the device key is a file readable by this user only");
        return new LocalIdentity(identity, store.Protection);
    }

    private async Task<LocalIdentity> LoadAsync(IdentityRecord record, CancellationToken cancellationToken)
    {
        byte[]? privateKey;
        try
        {
            using var store = await secrets.OpenAsync(record.Protection, cancellationToken).ConfigureAwait(false);
            privateKey = await store.LoadAsync(SecretName, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreUnavailableException e)
        {
            throw new IdentityUnavailableException(e.Message, e);
        }
        if (privateKey is null)
            throw new IdentityUnavailableException(
                $"The private key of device {record.DeviceId} is missing from the {record.Protection} store. Paired devices will not accept this device until it is paired again.");

        DeviceIdentity identity;
        try
        {
            identity = DeviceIdentity.FromPrivateKey(record.DeviceId, privateKey);
        }
        catch (CryptographicException e)
        {
            throw new IdentityUnavailableException("The stored device key is damaged.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }

        if (!identity.PublicKey.AsSpan().SequenceEqual(record.PublicKey))
        {
            identity.Dispose();
            throw new IdentityUnavailableException("The stored device key does not belong to identity.json.");
        }
        logger.LogInformation("Loaded device identity {DeviceId}, fingerprint {Fingerprint}", identity.Id, identity.Fingerprint.ToShortString());
        return new LocalIdentity(identity, record.Protection);
    }

    private IdentityRecord? ReadRecord()
    {
        if (!File.Exists(PublicPath))
            return null;
        try
        {
            var record = JsonSerializer.Deserialize(File.ReadAllText(PublicPath), IdentityJsonContext.Default.IdentityRecord);
            if (record is null || record.DeviceId == Guid.Empty || record.PublicKey.Length == 0)
                throw new JsonException("required fields are missing");
            return record;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IdentityUnavailableException($"{PublicPath} cannot be read: {e.Message}", e);
        }
    }

    private void WriteRecord(IdentityRecord record)
    {
        var temp = PublicPath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, IdentityJsonContext.Default.IdentityRecord));
        File.Move(temp, PublicPath, overwrite: true);
    }
}

internal sealed record IdentityRecord
{
    public int Version { get; init; } = 1;

    public Guid DeviceId { get; init; }

    /// <summary>SubjectPublicKeyInfo, Base64 in the file.</summary>
    public byte[] PublicKey { get; init; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter<SecretProtection>))]
    public SecretProtection Protection { get; init; }

    public DateTime CreatedAtUtc { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IdentityRecord))]
internal sealed partial class IdentityJsonContext : JsonSerializerContext;
