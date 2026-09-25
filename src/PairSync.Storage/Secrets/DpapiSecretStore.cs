using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PairSync.Storage.Secrets;

/// <summary>
/// Windows: <c>{name}.key</c> encrypted with DPAPI for the current user. Other accounts and other machines cannot
/// decrypt it; a password reset by an administrator makes it unreadable (the device then has to pair again).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string directory) : FileSecretStore(directory)
{
    public override SecretProtection Protection => SecretProtection.Dpapi;

    protected override byte[] Protect(string name, byte[] secret) =>
        ProtectedData.Protect(secret, Entropy(name), DataProtectionScope.CurrentUser);

    protected override byte[] Unprotect(string name, byte[] stored)
    {
        try
        {
            return ProtectedData.Unprotect(stored, Entropy(name), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException e)
        {
            throw new SecretStoreUnavailableException(
                "The device key cannot be decrypted by this Windows account (other user, restored backup or reset password).", e);
        }
    }

    /// <summary>Binds each blob to its purpose, so one secret file cannot be swapped for another.</summary>
    private static byte[] Entropy(string name) => Encoding.UTF8.GetBytes("PairSync secret v1:" + name);
}
