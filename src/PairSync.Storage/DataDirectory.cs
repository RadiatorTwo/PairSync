namespace PairSync.Storage;

/// <summary>
/// Where PairSync keeps its state: Windows <c>%APPDATA%\PairSync</c>, Linux <c>$XDG_DATA_HOME/pairsync</c>
/// (else <c>~/.local/share/pairsync</c>). Tests pass their own root.
/// </summary>
public sealed class DataDirectory(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string DatabasePath => Path.Combine(Root, "pairsync.db");

    /// <summary>Protected private key of the device identity (work package B).</summary>
    public string IdentityKeyPath => Path.Combine(Root, "identity.key");

    public string SettingsPath => Path.Combine(Root, "settings.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public static DataDirectory Default() => new(DefaultRoot());

    public static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PairSync");

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        // The XDG spec says relative values are invalid and must be ignored.
        var dataHome = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "pairsync");
    }

    /// <summary>Creates the folders; on Unix the root is accessible by the owner only.</summary>
    public void EnsureCreated()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(Root);
        else
            Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(LogsDirectory);
    }
}
