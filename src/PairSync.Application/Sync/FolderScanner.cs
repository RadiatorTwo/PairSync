using System.Security.Cryptography;
using PairSync.Domain;
using PairSync.Protocol;

namespace PairSync.Application.Sync;

/// <summary>What a scan found compared with the local index.</summary>
/// <param name="Changes">New, changed and deleted entries with a raised version; to be recorded with new sequences.</param>
/// <param name="Touched">Same content, new modification time only; recorded without a new version.</param>
/// <param name="Problems">Names that cannot be synced.</param>
/// <param name="Busy">Files that were still being written; scan again soon.</param>
public sealed record ScanResult(IReadOnlyList<SyncFile> Changes, IReadOnlyList<SyncFile> Touched, IReadOnlyList<string> Problems, bool Busy);

/// <summary>The profile folder is missing or unreadable; the scan did not report anything as deleted.</summary>
public sealed class SyncFolderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Walks a profile folder and compares it with the local index (phase 3 block B). A file whose size and modification
/// time match its entry is not read again; otherwise SHA-256 and the chunk hashes are computed, and a change raises
/// this device's counter in the version. Paths that disappeared become tombstones. Symbolic links, excluded paths and
/// files still being written are left out.
/// </summary>
public sealed class FolderScanner(Guid deviceId, TimeProvider time)
{
    /// <summary>A file changed this recently may still be written.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    public ScanResult Scan(string root, ExcludeRules excludes, IReadOnlyDictionary<string, SyncFile> index, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            throw new SyncFolderUnavailableException($"The folder {root} does not exist or cannot be reached.");

        var now = time.GetUtcNow().UtcDateTime;
        var changes = new List<SyncFile>();
        var touched = new List<SyncFile>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var busy = false;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemInfo[] children;
            try
            {
                children = folder.GetFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (folder.FullName == new DirectoryInfo(root).FullName)
                    throw new SyncFolderUnavailableException($"The folder {root} cannot be read: {e.Message}", e);
                problems.Add($"{SyncPaths.Relative(root, folder.FullName)}: cannot be read ({e.Message})");
                // Keep what the index knows below it instead of deleting it on the other device.
                MarkSeenBelow(SyncPaths.Relative(root, folder.FullName), index, seen);
                continue;
            }

            foreach (var child in children)
            {
                if (child.LinkTarget is not null)
                    continue;
                var path = SyncPaths.Relative(root, child.FullName);
                var isDirectory = child is DirectoryInfo;
                if (excludes.IsExcluded(path, isDirectory))
                    continue;
                if (SyncPaths.Check(path) is { } problem)
                {
                    problems.Add(problem);
                    continue;
                }
                seen.Add(path);
                index.TryGetValue(path, out var previous);

                if (child is DirectoryInfo directory)
                {
                    pending.Push(directory);
                    if (previous is { Deleted: false, IsDirectory: true })
                        continue;
                    changes.Add(new SyncFile { Path = path, IsDirectory = true, MTimeUtc = child.LastWriteTimeUtc, Version = Next(previous) });
                    continue;
                }

                var file = (FileInfo)child;
                if (previous is { Deleted: false, IsDirectory: false } && previous.Size == file.Length && previous.MTimeUtc == file.LastWriteTimeUtc)
                    continue;
                if (now - file.LastWriteTimeUtc < SettleTime)
                {
                    busy = true;
                    KeepPrevious(path, previous, seen);
                    continue;
                }

                FileHashes hashes;
                try
                {
                    hashes = FileHashes.Compute(file.FullName, cancellationToken);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Locked or being written: try again with the next scan, the old entry stays.
                    busy = true;
                    KeepPrevious(path, previous, seen);
                    continue;
                }
                file.Refresh();
                if (file.Length != hashes.Size || now - file.LastWriteTimeUtc < SettleTime)
                {
                    busy = true;
                    KeepPrevious(path, previous, seen);
                    continue;
                }

                var entry = new SyncFile
                {
                    Path = path,
                    Size = hashes.Size,
                    Sha256 = hashes.Sha256,
                    ChunkHashes = hashes.ChunkHashes,
                    MTimeUtc = file.LastWriteTimeUtc,
                };
                if (previous is { Deleted: false, IsDirectory: false } && previous.SameContentAs(entry))
                {
                    entry.Version = previous.Version;
                    entry.Sequence = previous.Sequence;
                    touched.Add(entry);
                }
                else
                {
                    entry.Version = Next(previous);
                    changes.Add(entry);
                }
            }
        }

        foreach (var (path, previous) in index)
        {
            if (previous.Deleted || seen.Contains(path) || excludes.IsExcludedWithParents(path, previous.IsDirectory))
                continue;
            changes.Add(new SyncFile { Path = path, IsDirectory = previous.IsDirectory, Deleted = true, DeletedAtUtc = now, MTimeUtc = now, Version = Next(previous) });
        }
        return new ScanResult(changes, touched, problems, busy);
    }

    private string Next(SyncFile? previous) => (previous?.VersionVector ?? VersionVector.Empty).Increment(deviceId).ToString();

    private static void KeepPrevious(string path, SyncFile? previous, HashSet<string> seen)
    {
        if (previous is not null)
            seen.Add(path);
    }

    private static void MarkSeenBelow(string folder, IReadOnlyDictionary<string, SyncFile> index, HashSet<string> seen)
    {
        var prefix = folder + "/";
        seen.Add(folder);
        foreach (var path in index.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)))
            seen.Add(path);
    }
}

/// <summary>SHA-256 of a file and of each of its 4 MiB chunks, read once.</summary>
public sealed record FileHashes(long Size, byte[] Sha256, byte[] ChunkHashes)
{
    public static int ChunkCount(long size) => (int)Math.Max(1, (size + ProtocolLimits.ChunkSize - 1) / ProtocolLimits.ChunkSize);

    public static FileHashes Compute(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = new MemoryStream();
        var buffer = new byte[ProtocolLimits.ChunkSize];
        long size = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filled = 0;
            int read;
            while (filled < buffer.Length && (read = stream.Read(buffer, filled, buffer.Length - filled)) > 0)
                filled += read;
            if (filled == 0 && size > 0)
                break;
            var chunk = buffer.AsSpan(0, filled);
            whole.AppendData(chunk);
            chunks.Write(SHA256.HashData(chunk));
            size += filled;
            if (filled < buffer.Length)
                break;
        }
        return new FileHashes(size, whole.GetHashAndReset(), chunks.ToArray());
    }
}
