namespace PairSync.Storage.Secrets;

/// <summary>
/// Fallback without an OS keyring: <c>{name}.key</c> in the data directory, mode 0600 on Unix. Protects against other
/// users, not against software running as the same user.
/// </summary>
public class FileSecretStore(string directory) : ISecretStore
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public virtual SecretProtection Protection => SecretProtection.File;

    public string PathFor(string name)
    {
        SecretNames.Validate(name);
        return Path.Combine(directory, name + ".key");
    }

    public async Task<byte[]?> LoadAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            return null;
        try
        {
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~OwnerOnly) != 0)
                File.SetUnixFileMode(path, OwnerOnly); // someone widened the rights; narrow them again
            return Unprotect(name, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreUnavailableException($"The secret file {path} cannot be read: {e.Message}", e);
        }
    }

    /// <summary>Writes a sibling file with owner-only rights and replaces the secret, so a crash never leaves it half-written.</summary>
    public async Task SaveAsync(string name, byte[] secret, CancellationToken cancellationToken)
    {
        var path = PathFor(name);
        var temp = path + ".new";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        await using (var stream = new FileStream(temp, options))
        {
            await stream.WriteAsync(Protect(name, secret), cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        File.Delete(PathFor(name));
        return Task.CompletedTask;
    }

    protected virtual byte[] Protect(string name, byte[] secret) => secret;

    protected virtual byte[] Unprotect(string name, byte[] stored) => stored;

    public void Dispose()
    {
    }
}
