using System.Text.RegularExpressions;

namespace PairSync.Application.Claude;

/// <summary>
/// Absolute paths in commands (hooks, status line, MCP servers). The source replaces its configuration and home folder
/// with <c>${CLAUDE_CONFIG_DIR}</c> and <c>${HOME}</c>; the target puts in its own values and its path variables.
/// Other absolute paths need a mapping chosen by the user.
/// </summary>
public static partial class PathMapper
{
    public const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";
    public const string HomeVariable = "HOME";

    // A quoted absolute path (may contain spaces) or an unquoted one that does not continue a word, URL or variable.
    [GeneratedRegex("""(?<q>['"])(?<p>(?:[A-Za-z]:[\\/]|/)[^'"]+)\k<q>|(?<![\w$}:/.\\~-])(?<p>(?:[A-Za-z]:[\\/]|/(?=[\w.~-]))[^\s'"`;|&<>(),]*)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePath();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();

    /// <summary>Replaces the source's own folders by variables; other absolute paths are left and reported.</summary>
    /// <param name="wholeValue">The text is one argument: if it is an absolute path, all of it is (spaces included).</param>
    public static string Generalize(string text, ClaudeEnvironment source, bool windows, ICollection<string> unmapped, bool wholeValue = false)
    {
        if (wholeValue && IsAbsolute(text))
            return GeneralizePath(text, source, windows, unmapped);
        return AbsolutePath().Replace(text, match =>
        {
            var path = match.Groups["p"].Value;
            var replaced = GeneralizePath(path, source, windows, unmapped);
            return match.Value.Replace(path, replaced, StringComparison.Ordinal);
        });
    }

    private static bool IsAbsolute(string text) =>
        text.StartsWith('/') || (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/');

    private static string GeneralizePath(string path, ClaudeEnvironment source, bool windows, ICollection<string> unmapped)
    {
        foreach (var (folder, variable) in new[] { (source.ConfigDir, ConfigDirVariable), (source.HomeDir, HomeVariable) })
        {
            var prefix = Normalize(folder);
            var candidate = Normalize(path);
            var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (prefix.Length > 0 && candidate.StartsWith(prefix, comparison) && (candidate.Length == prefix.Length || candidate[prefix.Length] == '/'))
                return $"${{{variable}}}{candidate[prefix.Length..]}";
        }
        if (!unmapped.Contains(path))
            unmapped.Add(path);
        return path;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>Applies the user's mappings (original path → replacement) chosen on the source.</summary>
    public static string ApplyMappings(string text, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var (original, replacement) in mappings.OrderByDescending(m => m.Key.Length))
            text = text.Replace(original, replacement, StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    /// On the target: puts in the known variables. Unknown ones stay as they are, e.g. <c>${CLAUDE_PLUGIN_ROOT}</c>
    /// or secret references Claude Code expands itself.
    /// </summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string> variables) =>
        Variable().Replace(text, match => variables.TryGetValue(match.Groups[1].Value, out var value) ? value.Replace('\\', '/').TrimEnd('/') : match.Value);

    /// <summary>The variables the target knows: its folders plus the user's path variables (<c>NAME=value</c> lines).</summary>
    public static Dictionary<string, string> TargetVariables(ClaudeEnvironment target, IEnumerable<string> pathVariables)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in pathVariables)
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            var name = line[..equals].Trim();
            if (Variable().IsMatch($"${{{name}}}"))
                result[name] = line[(equals + 1)..].Trim();
        }
        result[ConfigDirVariable] = target.ConfigDir;
        result[HomeVariable] = target.HomeDir;
        return result;
    }
}
