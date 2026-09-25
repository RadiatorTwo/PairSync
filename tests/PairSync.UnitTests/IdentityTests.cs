using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PairSync.Domain;
using PairSync.Storage;
using PairSync.Storage.Identity;
using PairSync.Storage.Secrets;

namespace PairSync.UnitTests;

public sealed class DeviceIdentityTests
{
    [Fact]
    public void Fingerprint_is_sha256_of_the_public_key_in_colon_hex()
    {
        using var identity = DeviceIdentity.Create();
        var hash = SHA256.HashData(identity.PublicKey);

        var full = identity.Fingerprint.ToString();
        var parts = full.Split(':');
        Assert.Equal(32, parts.Length);
        Assert.Equal(Convert.ToHexString(hash), string.Concat(parts));
        Assert.Equal($"SHA256: {string.Join(':', parts[..4])}:…:{string.Join(':', parts[^2..])}", identity.Fingerprint.ToShortString());
    }

    [Fact]
    public void Fingerprints_compare_by_value()
    {
        using var identity = DeviceIdentity.Create();
        Assert.Equal(identity.Fingerprint, DeviceFingerprint.Of(identity.PublicKey.ToArray()));
        using var other = DeviceIdentity.Create();
        Assert.NotEqual(identity.Fingerprint, other.Fingerprint);
    }

    [Fact]
    public void Private_key_round_trips()
    {
        using var identity = DeviceIdentity.Create();
        using var restored = DeviceIdentity.FromPrivateKey(identity.Id, identity.ExportPrivateKey());

        Assert.Equal(identity.Id, restored.Id);
        Assert.Equal(identity.PublicKey, restored.PublicKey);
        var signature = restored.Sign("hello"u8);
        Assert.True(DeviceIdentity.Verify(identity.PublicKey, "hello"u8, signature));
    }

    [Fact]
    public void Signature_does_not_verify_with_another_key_or_other_data()
    {
        using var identity = DeviceIdentity.Create();
        using var other = DeviceIdentity.Create();
        var signature = identity.Sign("hello"u8);

        Assert.False(DeviceIdentity.Verify(other.PublicKey, "hello"u8, signature));
        Assert.False(DeviceIdentity.Verify(identity.PublicKey, "hellO"u8, signature));
        Assert.False(DeviceIdentity.Verify([1, 2, 3], "hello"u8, signature));
    }

    [Fact]
    public void Foreign_or_damaged_keys_are_rejected()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.ThrowsAny<CryptographicException>(() => DeviceIdentity.FromPrivateKey(Guid.NewGuid(), p384.ExportPkcs8PrivateKey()));
        Assert.ThrowsAny<CryptographicException>(() => DeviceIdentity.FromPrivateKey(Guid.NewGuid(), [0x30, 0x03, 0x02, 0x01, 0x00]));
    }

    [Fact]
    public void Certificate_carries_the_device_key()
    {
        using var identity = DeviceIdentity.Create();
        using var certificate = identity.CreateCertificate(TimeProvider.System);

        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(identity.PublicKey, DeviceIdentity.PublicKeyOf(certificate));
        Assert.Contains(identity.Id.ToString("D"), certificate.Subject, StringComparison.Ordinal);
        Assert.True(certificate.NotAfter > DateTime.Now.AddYears(9));
        Assert.True(certificate.NotBefore < DateTime.Now);
    }
}

public sealed class SecretStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-secrets-");

    public void Dispose() => _root.Delete(recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Kinds => OperatingSystem.IsWindows() ? ["file", "dpapi"] : ["file"];

    private ISecretStore Open(string kind) =>
        kind == "dpapi" && OperatingSystem.IsWindows() ? new DpapiSecretStore(_root.FullName) : new FileSecretStore(_root.FullName);

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Secret_round_trips_and_can_be_deleted(string kind)
    {
        using var store = Open(kind);
        Assert.Null(await store.LoadAsync("identity", Ct));

        await store.SaveAsync("identity", [1, 2, 3], Ct);
        await store.SaveAsync("identity", [4, 5, 6], Ct);
        Assert.Equal([4, 5, 6], await store.LoadAsync("identity", Ct));

        await store.DeleteAsync("identity", Ct);
        Assert.Null(await store.LoadAsync("identity", Ct));
        Assert.Empty(_root.GetFiles());
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("with space")]
    public async Task Names_are_restricted(string name)
    {
        using var store = new FileSecretStore(_root.FullName);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(name, [1], Ct));
    }

    [Fact]
    public async Task File_store_is_owner_only_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        if (OperatingSystem.IsWindows())
            return;
        using var store = new FileSecretStore(_root.FullName);
        await store.SaveAsync("identity", [1], Ct);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.PathFor("identity")));
    }

    [Fact]
    public async Task Dpapi_file_does_not_contain_the_secret_and_cannot_be_swapped()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows only");
        if (!OperatingSystem.IsWindows())
            return;
        using var store = new DpapiSecretStore(_root.FullName);
        var secret = RandomNumberGenerator.GetBytes(64);
        await store.SaveAsync("identity", secret, Ct);

        var stored = await File.ReadAllBytesAsync(store.PathFor("identity"), Ct);
        Assert.Equal(-1, stored.AsSpan().IndexOf(secret.AsSpan(0, 16)));

        // A blob copied under another name fails: the name is part of the DPAPI entropy.
        File.Copy(store.PathFor("identity"), store.PathFor("other"));
        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => store.LoadAsync("other", Ct));
    }
}

public sealed class DeviceIdentityStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pairsync-identity-");

    public void Dispose() => _root.Delete(recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DataDirectory Data => new(_root.FullName);

    private DeviceIdentityStore Store(ISecretStoreProvider? secrets = null) =>
        new(Data, secrets ?? new FileSecrets(_root.FullName), NullLogger<DeviceIdentityStore>.Instance);

    [Fact]
    public async Task First_start_creates_and_later_starts_load_the_same_identity()
    {
        var created = await Store().LoadOrCreateAsync(Ct);
        var loaded = await Store().LoadOrCreateAsync(Ct);
        using (created.Identity)
        using (loaded.Identity)
        {
            Assert.Equal(created.Identity.Id, loaded.Identity.Id);
            Assert.Equal(created.Identity.PublicKey, loaded.Identity.PublicKey);
            Assert.Equal(SecretProtection.File, loaded.Protection);
            Assert.NotEqual(Guid.Empty, loaded.Identity.Id);
        }
        Assert.Contains("\"protection\": \"File\"", await File.ReadAllTextAsync(Store().PublicPath, Ct));
    }

    [Fact]
    public async Task Missing_private_key_is_an_error_not_a_new_identity()
    {
        (await Store().LoadOrCreateAsync(Ct)).Identity.Dispose();
        var before = await File.ReadAllTextAsync(Store().PublicPath, Ct);
        File.Delete(Path.Combine(_root.FullName, "identity.key"));

        await Assert.ThrowsAsync<IdentityUnavailableException>(() => Store().LoadOrCreateAsync(Ct));
        Assert.Equal(before, await File.ReadAllTextAsync(Store().PublicPath, Ct));
    }

    [Fact]
    public async Task Private_key_of_another_identity_is_rejected()
    {
        (await Store().LoadOrCreateAsync(Ct)).Identity.Dispose();
        using var stranger = DeviceIdentity.Create();
        using (var secrets = new FileSecretStore(_root.FullName))
            await secrets.SaveAsync(DeviceIdentityStore.SecretName, stranger.ExportPrivateKey(), Ct);

        await Assert.ThrowsAsync<IdentityUnavailableException>(() => Store().LoadOrCreateAsync(Ct));
    }

    [Fact]
    public async Task Damaged_identity_file_is_an_error()
    {
        await File.WriteAllTextAsync(Store().PublicPath, "{ broken", Ct);
        await Assert.ThrowsAsync<IdentityUnavailableException>(() => Store().LoadOrCreateAsync(Ct));
    }

    [Fact]
    public async Task Unavailable_keyring_is_reported()
    {
        (await Store().LoadOrCreateAsync(Ct)).Identity.Dispose();
        var error = await Assert.ThrowsAsync<IdentityUnavailableException>(() => Store(new UnavailableSecrets()).LoadOrCreateAsync(Ct));
        Assert.Contains("keyring", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FileSecrets(string root) : ISecretStoreProvider
    {
        public Task<ISecretStore> OpenPreferredAsync(CancellationToken cancellationToken) => Task.FromResult<ISecretStore>(new FileSecretStore(root));

        public Task<ISecretStore> OpenAsync(SecretProtection protection, CancellationToken cancellationToken) => OpenPreferredAsync(cancellationToken);
    }

    private sealed class UnavailableSecrets : ISecretStoreProvider
    {
        public Task<ISecretStore> OpenPreferredAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ISecretStore> OpenAsync(SecretProtection protection, CancellationToken cancellationToken) =>
            throw new SecretStoreUnavailableException("The keyring is locked.");
    }
}
