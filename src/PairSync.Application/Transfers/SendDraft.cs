using PairSync.Domain;

namespace PairSync.Application.Transfers;

/// <summary>A row of the Send table: one chosen file or folder.</summary>
public sealed record DraftEntry(string Name, string SourcePath, bool IsFolder, long Size, int FileCount);

/// <summary>One file (or empty folder) of the job, with its path relative to the job root.</summary>
public sealed record DraftItem(string RelativePath, string SourcePath, long Size, DateTime LastWriteTimeUtc, bool IsDirectory);

/// <summary>What the user picked on the Send screen, flattened into items (plan §8).</summary>
public sealed record SendDraft(
    IReadOnlyList<DraftEntry> Entries, IReadOnlyList<DraftItem> Items, IReadOnlyList<string> SkippedLinks, IReadOnlyList<string> Problems)
{
    public long TotalBytes => Items.Sum(i => i.Size);

    public int FileCount => Items.Count(i => !i.IsDirectory);
}

/// <summary>
/// Collects files and folders for a job. Folders are walked recursively; each item gets a relative path below the
/// chosen folder's name. Links that point outside the chosen folder are skipped (mockup hint), as are links to
/// folders inside it, whose content is already part of the job. Names another OS cannot store are reported.
/// </summary>
public static class SendScanner
{
    public static SendDraft Scan(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var entries = new List<DraftEntry>();
        var items = new List<DraftItem>();
        var skippedLinks = new List<string>();
        var problems = new List<string>();
        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(path);
            var isFolder = Directory.Exists(full);
            if (!isFolder && !File.Exists(full))
            {
                problems.Add($"{path}: not found");
                continue;
            }

            var name = UniqueName(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)), topNames);
            if (RelativePaths.CheckSegment(name) is { } problem)
            {
                problems.Add($"{full}: {problem}");
                continue;
            }

            if (!isFolder)
            {
                var file = new FileInfo(full);
                var target = file.LinkTarget is null ? file : file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;
                if (target is not { Exists: true })
                {
                    skippedLinks.Add(full);
                    continue;
                }
                items.Add(new DraftItem(name, target.FullName, target.Length, target.LastWriteTimeUtc, false));
                entries.Add(new DraftEntry(name, full, false, target.Length, 1));
                continue;
            }

            var before = items.Count;
            WalkFolder(new DirectoryInfo(full), name, items, skippedLinks, problems, cancellationToken);
            var added = items.Skip(before).ToList();
            entries.Add(new DraftEntry(name, full, true, added.Sum(i => i.Size), added.Count(i => !i.IsDirectory)));
        }

        return new SendDraft(entries, items, skippedLinks, problems);
    }

    private static void WalkFolder(
        DirectoryInfo root, string rootName, List<DraftItem> items, List<string> skippedLinks, List<string> problems,
        CancellationToken cancellationToken)
    {
        var rootPrefix = Path.EndsInDirectorySeparator(root.FullName) ? root.FullName : root.FullName + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var pending = new Stack<(DirectoryInfo Folder, string Relative)>();
        pending.Push((root, rootName));

        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemInfo[] children;
            try
            {
                children = current.Folder.GetFileSystemInfos();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                problems.Add($"{current.Folder.FullName}: {e.Message}");
                continue;
            }

            if (children.Length == 0 && current.Folder != root)
                items.Add(new DraftItem(current.Relative, current.Folder.FullName, 0, current.Folder.LastWriteTimeUtc, true));

            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var relative = $"{current.Relative}/{child.Name}";
                if (RelativePaths.CheckSegment(child.Name) is { } problem)
                {
                    problems.Add($"{child.FullName}: {problem}");
                    continue;
                }

                if (child.LinkTarget is not null)
                {
                    FileSystemInfo? target;
                    try
                    {
                        target = child.ResolveLinkTarget(returnFinalTarget: true);
                    }
                    catch (IOException)
                    {
                        target = null;
                    }
                    if (target is FileInfo { Exists: true } file && file.FullName.StartsWith(rootPrefix, comparison))
                        items.Add(new DraftItem(relative, file.FullName, file.Length, file.LastWriteTimeUtc, false));
                    else
                        skippedLinks.Add(child.FullName);
                    continue;
                }

                switch (child)
                {
                    case DirectoryInfo folder:
                        pending.Push((folder, relative));
                        break;
                    case FileInfo file:
                        items.Add(new DraftItem(relative, file.FullName, file.Length, file.LastWriteTimeUtc, false));
                        break;
                }
            }
        }
    }

    /// <summary>Two picked items with the same name (from different folders) get "name (2)".</summary>
    private static string UniqueName(string name, HashSet<string> taken)
    {
        if (name.Length == 0)
            name = "Drive";
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
        return candidate;
    }
}
