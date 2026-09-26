using System.Text;
using System.Text.RegularExpressions;

namespace PairSync.Application.Sync;

/// <summary>
/// Exclude patterns of a profile, gitignore-like: one per line, <c>#</c> starts a comment, <c>!</c> includes again,
/// a trailing <c>/</c> matches folders only, a leading or inner <c>/</c> anchors the pattern at the profile folder
/// (otherwise it matches a name at any depth), <c>*</c> and <c>?</c> stay within a name, <c>**</c> spans folders.
/// The last matching line wins. The profile's own folder <c>.pairsync</c> and transfer files are always excluded.
/// </summary>
public sealed class ExcludeRules
{
    public const string MetadataFolder = ".pairsync";

    public const string TempSuffix = ".pairsync-tmp";

    public static readonly ExcludeRules None = new([]);

    private readonly IReadOnlyList<Rule> _rules;

    private ExcludeRules(IReadOnlyList<Rule> rules) => _rules = rules;

    private sealed record Rule(Regex Pattern, bool Include, bool DirectoryOnly);

    public static ExcludeRules Parse(string? text)
    {
        var rules = new List<Rule>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var include = line.StartsWith('!');
            if (include)
                line = line[1..];
            var directoryOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0)
                continue;
            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            rules.Add(new Rule(ToRegex(line, anchored), include, directoryOnly));
        }
        return new ExcludeRules(rules);
    }

    /// <summary>Whether a path (relative, forward slashes) is left out. An excluded folder leaves out everything below it.</summary>
    public bool IsExcluded(string path, bool isDirectory)
    {
        if (IsAlwaysExcluded(path))
            return true;
        var excluded = false;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
                continue;
            if (rule.Pattern.IsMatch(path))
                excluded = !rule.Include;
        }
        return excluded;
    }

    /// <summary>Like <see cref="IsExcluded"/>, and also if a folder above the path is excluded (paths from the other device).</summary>
    public bool IsExcludedWithParents(string path, bool isDirectory)
    {
        if (IsExcluded(path, isDirectory))
            return true;
        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (IsExcluded(path[..slash], isDirectory: true))
                return true;
        }
        return false;
    }

    public static bool IsAlwaysExcluded(string path) =>
        path == MetadataFolder || path.StartsWith(MetadataFolder + "/", StringComparison.Ordinal) ||
        path.EndsWith(TempSuffix, StringComparison.Ordinal);

    private static Regex ToRegex(string pattern, bool anchored)
    {
        var regex = new StringBuilder(anchored ? "^" : "(^|/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // "**/" matches zero or more folders, a trailing "**" everything below.
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                regex.Append(slash ? "(.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }
        regex.Append('$');
        return new Regex(regex.ToString(), RegexOptions.CultureInvariant | (SyncPaths.CaseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None));
    }
}
