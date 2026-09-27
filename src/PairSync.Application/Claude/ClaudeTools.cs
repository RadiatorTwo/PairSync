using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PairSync.Application.Claude;

/// <summary>
/// Programs Claude Code and common plugins call: <c>git</c> (marketplaces, the Bash tool on Windows), <c>bun</c> and
/// <c>jq</c> (hooks of plugins such as claude-mem). Finds them also where installers put them after PairSync started,
/// so CLI steps get a PATH that includes those folders.
/// </summary>
public static class ClaudeTools
{
    public static IReadOnlyList<string> Names { get; } = ["git", "bun", "jq"];

    public static bool IsKnown(string name) => Names.Contains(name, StringComparer.Ordinal);

    /// <summary>The tools of <see cref="Names"/> this device lacks.</summary>
    public static IReadOnlyList<string> Missing(ClaudeOptions options) => [.. Names.Where(t => Find(t, options) is null)];

    /// <summary>The full path of <paramref name="tool"/>, or null.</summary>
    public static string? Find(string tool, ClaudeOptions options)
    {
        string[] names = OperatingSystem.IsWindows() ? [tool + ".exe", tool + ".cmd"] : [tool];
        foreach (var folder in Folders(options))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }
        }
        return null;
    }

    /// <summary>PATH for child processes: the inherited one plus the tool folders that exist.</summary>
    public static string SearchPath(ClaudeOptions options) =>
        string.Join(Path.PathSeparator, Folders(options).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

    /// <summary>PATH (process and, on Windows, the registry as it is now), then the installers' usual folders.</summary>
    public static IEnumerable<string> Folders(ClaudeOptions options)
    {
        var home = options.HomeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new List<string>();
        if (options.SearchSystemPath)
        {
            folders.AddRange(Split(Environment.GetEnvironmentVariable("PATH")));
            if (OperatingSystem.IsWindows())
            {
                folders.AddRange(RegistryPath());
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                folders.Add(Path.Combine(programFiles, "Git", "cmd"));
                folders.Add(Path.Combine(local, "Programs", "Git", "cmd"));
                folders.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
                folders.Add(Path.Combine(local, "Microsoft", "WindowsApps"));
            }
            else
            {
                folders.AddRange(["/usr/local/bin", "/usr/bin", "/bin", "/opt/homebrew/bin"]);
            }
        }
        folders.Add(Path.Combine(home, ".local", "bin"));
        folders.Add(Path.Combine(home, ".bun", "bin"));
        return folders.Where(f => f.Length > 0);
    }

    private static IEnumerable<string> Split(string? path) =>
        (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>User and machine PATH as stored now; installers change them while PairSync keeps its old copy.</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryPath()
    {
        var values = new List<string?>();
        using (var user = Registry.CurrentUser.OpenSubKey("Environment"))
            values.Add(user?.GetValue("Path") as string);
        using (var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
            values.Add(machine?.GetValue("Path") as string);
        return values.SelectMany(v => Split(v).Select(Environment.ExpandEnvironmentVariables));
    }
}
