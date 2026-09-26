using System.Text;
using PairSync.Domain;

namespace PairSync.Application.Sync;

/// <summary>
/// Paths in a sync index: relative to the profile folder, forward slashes, Unicode NFC, so the same name from Windows,
/// Linux or macOS is one entry. Every path from the other device is checked like a received job path (plan §11).
/// </summary>
public static class SyncPaths
{
    /// <summary>Whether this file system treats names that differ only in case as the same file.</summary>
    public static bool CaseInsensitive { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparer Comparer => CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Relative index path of <paramref name="fullPath"/> below <paramref name="root"/>.</summary>
    public static string Relative(string root, string fullPath) =>
        Normalize(Path.GetRelativePath(root, fullPath));

    public static string Normalize(string path) => path.Replace('\\', '/').Normalize(NormalizationForm.FormC);

    /// <summary>Why a path cannot be synced here, or null.</summary>
    public static string? Check(string path) => RelativePaths.Check(path);

    /// <summary>The full path of a checked index path; never outside <paramref name="root"/>.</summary>
    /// <exception cref="ArgumentException">The path is not valid.</exception>
    public static string Full(string root, string path) => RelativePaths.Combine(root, path);

    /// <summary>Metadata folder of a profile (<c>.pairsync</c>): transfers in progress and the trash.</summary>
    public static string MetadataFolder(string root) => Path.Combine(root, ExcludeRules.MetadataFolder);

    public static string TrashFolder(string root) => Path.Combine(MetadataFolder(root), "trash");

    public static string StagingFolder(string root) => Path.Combine(MetadataFolder(root), "tmp");
}
