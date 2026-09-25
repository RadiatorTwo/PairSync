namespace PairSync.Storage.Secrets;

/// <summary>Opens secret stores; tests replace it to force a specific kind.</summary>
public interface ISecretStoreProvider
{
    /// <summary>The best store this system offers: DPAPI on Windows, Secret Service on Linux, else the file fallback.</summary>
    Task<ISecretStore> OpenPreferredAsync(CancellationToken cancellationToken);

    /// <summary>Opens a specific kind, for secrets saved earlier with it.</summary>
    /// <exception cref="SecretStoreUnavailableException">That kind is not available right now.</exception>
    Task<ISecretStore> OpenAsync(SecretProtection protection, CancellationToken cancellationToken);
}

public sealed class PlatformSecretStores(DataDirectory dataDirectory) : ISecretStoreProvider
{
    public async Task<ISecretStore> OpenPreferredAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiSecretStore(dataDirectory.Root);
        return await SecretServiceStore.TryOpenAsync(dataDirectory.Root, cancellationToken).ConfigureAwait(false)
            ?? (ISecretStore)new FileSecretStore(dataDirectory.Root);
    }

    public async Task<ISecretStore> OpenAsync(SecretProtection protection, CancellationToken cancellationToken)
    {
        switch (protection)
        {
            case SecretProtection.Dpapi when OperatingSystem.IsWindows():
                return new DpapiSecretStore(dataDirectory.Root);
            case SecretProtection.Dpapi:
                throw new SecretStoreUnavailableException("The device key is protected with Windows DPAPI and cannot be read on this system.");
            case SecretProtection.SecretService:
                return await SecretServiceStore.TryOpenAsync(dataDirectory.Root, cancellationToken).ConfigureAwait(false)
                    ?? throw new SecretStoreUnavailableException(
                        "The device key is in the system keyring, but no Secret Service is reachable. Start GNOME Keyring or KWallet and open PairSync again.");
            case SecretProtection.File:
                return new FileSecretStore(dataDirectory.Root);
            default:
                throw new SecretStoreUnavailableException($"Unknown secret protection {protection}.");
        }
    }
}
