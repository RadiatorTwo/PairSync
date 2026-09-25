namespace PairSync.Storage.Secrets;

/// <summary>How a secret is protected at rest. Shown in Settings; <see cref="File"/> gets a warning there.</summary>
public enum SecretProtection
{
    /// <summary>Windows: DPAPI, bound to the user account.</summary>
    Dpapi = 0,

    /// <summary>Linux: Secret Service over D-Bus (GNOME Keyring, KWallet, KeePassXC).</summary>
    SecretService = 1,

    /// <summary>Fallback: plain file readable by the owner only (0600).</summary>
    File = 2,
}

/// <summary>Small secrets (the device private key) kept outside the database.</summary>
public interface ISecretStore : IDisposable
{
    SecretProtection Protection { get; }

    /// <returns>The secret, or null if none is stored under this name.</returns>
    /// <exception cref="SecretStoreUnavailableException">The store exists but cannot be read right now.</exception>
    Task<byte[]?> LoadAsync(string name, CancellationToken cancellationToken);

    Task SaveAsync(string name, byte[] secret, CancellationToken cancellationToken);

    Task DeleteAsync(string name, CancellationToken cancellationToken);
}

/// <summary>The store cannot be used right now: keyring locked or not running, prompt dismissed, file unreadable.</summary>
public sealed class SecretStoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

internal static class SecretNames
{
    /// <summary>Names become file names; keep them to a safe alphabet.</summary>
    public static void Validate(string name)
    {
        if (name.Length is 0 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException($"Invalid secret name '{name}'.", nameof(name));
    }
}
