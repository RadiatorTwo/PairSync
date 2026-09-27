using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PairSync.Application.Claude;

/// <summary>What <see cref="IUserPath.Ensure"/> did.</summary>
/// <param name="Changed">Files or registry values written; empty if the folder was on PATH already.</param>
public sealed record PathUpdate(string Folder, bool AlreadyOnPath, IReadOnlyList<string> Changed);

/// <summary>Puts a folder on the user's PATH for new terminals (the Claude Code installer does not do it itself).</summary>
public interface IUserPath
{
    /// <exception cref="IOException">A file or the registry value could not be written.</exception>
    PathUpdate Ensure(string folder);

    public static IUserPath ForCurrentPlatform(string homeDir) =>
        OperatingSystem.IsWindows() ? new WindowsUserPath() : new ShellUserPath(homeDir);
}

/// <summary>
/// Windows: the user's <c>Path</c> in <c>HKCU\Environment</c> (kept as REG_EXPAND_SZ, other entries untouched),
/// then WM_SETTINGCHANGE so Explorer and new terminals see it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUserPath(string keyPath = "Environment") : IUserPath
{
    public PathUpdate Ensure(string folder)
    {
        var full = Path.GetFullPath(folder);
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new IOException($@"HKCU\{keyPath} cannot be opened.");
        var raw = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var entries = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Any(e => Same(Environment.ExpandEnvironmentVariables(e), full)))
            return new PathUpdate(full, true, []);

        // Written with %USERPROFILE% where possible, like Windows' own dialogs do.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var entry = full.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + full[profile.Length..]
            : full;
        key.SetValue("Path", string.Join(';', [.. entries, entry]), RegistryValueKind.ExpandString);
        Broadcast();
        AddToProcess(full);
        return new PathUpdate(full, false, [$@"HKCU\{keyPath}\Path"]);
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static void AddToProcess(string folder)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!current.Split(';').Any(e => Same(e, folder)))
            Environment.SetEnvironmentVariable("PATH", current.Length == 0 ? folder : current + ";" + folder);
    }

    private static void Broadcast() =>
        _ = SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

/// <summary>
/// Linux and macOS: an <c>export PATH</c> line in <c>~/.profile</c> (login sessions) and in <c>~/.bashrc</c> /
/// <c>~/.zshrc</c> if they exist, plus <c>conf.d/pairsync-path.fish</c> for fish (CachyOS' default shell).
/// Each file gets the block once; files that already mention the folder are left alone.
/// </summary>
public sealed class ShellUserPath(string homeDir) : IUserPath
{
    public const string Marker = "# Added by PairSync: Claude Code";

    public PathUpdate Ensure(string folder)
    {
        var full = Path.GetFullPath(folder);
        var home = Path.GetFullPath(homeDir);
        var relative = Path.GetRelativePath(home, full);
        var shellValue = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? full
            : "$HOME/" + relative.Replace('\\', '/');
        var changed = new List<string>();
        var block = $"\n{Marker}\ncase \":$PATH:\" in *\":{shellValue}:\"*) ;; *) export PATH=\"{shellValue}:$PATH\" ;; esac\n";

        AppendOnce(Path.Combine(home, ".profile"), block, shellValue, full, create: true, changed);
        foreach (var rc in new[] { ".bashrc", ".zshrc" })
            AppendOnce(Path.Combine(home, rc), block, shellValue, full, create: false, changed);

        var fish = Path.Combine(home, ".config", "fish");
        if (Directory.Exists(fish))
        {
            var file = Path.Combine(fish, "conf.d", "pairsync-path.fish");
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, $"{Marker}\nfish_add_path -g \"{shellValue}\"\n");
                changed.Add(file);
            }
        }
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(e => e.TrimEnd('/') == full.TrimEnd('/'));
        if (!onPath)
            Environment.SetEnvironmentVariable("PATH", full + ":" + Environment.GetEnvironmentVariable("PATH"));
        return new PathUpdate(full, onPath && changed.Count == 0, changed);
    }

    private static void AppendOnce(string file, string block, string shellValue, string full, bool create, List<string> changed)
    {
        if (!File.Exists(file) && !create)
            return;
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        if (text.Contains(Marker, StringComparison.Ordinal) || text.Contains(shellValue, StringComparison.Ordinal)
            || text.Contains(full, StringComparison.Ordinal))
            return;
        File.AppendAllText(file, block);
        changed.Add(file);
    }
}
