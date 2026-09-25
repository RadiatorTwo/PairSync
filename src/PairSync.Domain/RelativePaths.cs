namespace PairSync.Domain;

/// <summary>
/// Relative paths of received items (plan §11). Every path comes from another device and is checked before anything
/// is written: forward slashes only, no empty, <c>.</c> or <c>..</c> segments, nothing absolute, no characters or
/// names Windows cannot store (so a job from Linux lands on Windows too), bounded length.
/// </summary>
public static class RelativePaths
{
    public const int MaxSegmentLength = 255;
    public const int MaxPathLength = 1024;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly char[] ForbiddenChars = ['<', '>', ':', '"', '\\', '|', '?', '*'];

    /// <summary>Why <paramref name="path"/> is not acceptable, or null if it is.</summary>
    public static string? Check(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "empty path";
        if (path.Length > MaxPathLength)
            return "path too long";
        foreach (var segment in path.Split('/'))
        {
            if (CheckSegment(segment) is { } problem)
                return $"'{path}': {problem}";
        }
        return null;
    }

    public static bool IsValid(string? path) => Check(path) is null;

    /// <summary>Checks a single file or folder name.</summary>
    public static string? CheckSegment(string segment)
    {
        if (segment.Length == 0)
            return "empty name (absolute path or double slash)";
        if (segment is "." or "..")
            return "relative navigation";
        if (segment.Length > MaxSegmentLength)
            return "name too long";
        if (segment.Any(c => char.IsControl(c) || ForbiddenChars.Contains(c)))
            return "name contains a character that is not allowed";
        if (segment[^1] is ' ' or '.')
            return "name ends with a space or dot";
        var stem = segment.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
            return "reserved device name";
        return null;
    }

    /// <summary>
    /// Finds two paths that would be the same on a case-insensitive file system, including a file that has the name
    /// of a folder another path needs. Returns the first clash or null.
    /// </summary>
    public static (string First, string Second)? FindCaseCollision(IEnumerable<(string Path, bool IsDirectory)> items)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, isDirectory) in items)
        {
            var segments = path.Split('/');
            for (var i = 1; i < segments.Length + (isDirectory ? 1 : 0); i++)
            {
                var folder = string.Join('/', segments[..i]);
                if (files.TryGetValue(folder, out var file))
                    return (file, path);
                if (directories.TryGetValue(folder, out var existing))
                {
                    if (!string.Equals(existing, folder, StringComparison.Ordinal))
                        return (existing, path);
                }
                else
                {
                    directories[folder] = folder;
                }
            }
            if (isDirectory)
                continue;
            if (files.TryGetValue(path, out var other) || directories.TryGetValue(path, out other))
                return (other, path);
            files[path] = path;
        }
        return null;
    }

    /// <summary>Joins a checked relative path onto <paramref name="root"/>; the result is always inside it.</summary>
    /// <exception cref="ArgumentException">The path is not valid.</exception>
    public static string Combine(string root, string relativePath)
    {
        if (Check(relativePath) is { } problem)
            throw new ArgumentException(problem, nameof(relativePath));
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine([fullRoot, .. relativePath.Split('/')]));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException($"'{relativePath}' leaves the target folder.", nameof(relativePath));
        return full;
    }
}
