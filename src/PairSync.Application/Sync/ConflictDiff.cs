using System.Text;
using PairSync.Domain;

namespace PairSync.Application.Sync;

public enum ConflictComparisonKind
{
    /// <summary>Text on both sides; <see cref="ConflictComparison.Hunks"/> holds the differing lines.</summary>
    Text,

    /// <summary>Same lines, only the line endings (CRLF/LF) or the byte order mark differ.</summary>
    LineEndingsOnly,

    /// <summary>At least one version is not UTF-8 text.</summary>
    Binary,

    TooLarge,

    /// <summary>More changed lines than a comparison shows.</summary>
    TooManyChanges,

    /// <summary>One version is not on this device (deleted, moved away or not fetched yet).</summary>
    Missing,
}

/// <summary>Lines that differ at one place: this device's version on the left, the other device's on the right.</summary>
/// <param name="LocalStart">First line (1-based) on this device; with no lines, the line the other side's lines come after.</param>
public sealed record DiffHunk(int LocalStart, IReadOnlyList<string> LocalLines, int RemoteStart, IReadOnlyList<string> RemoteLines);

/// <param name="MoreHunks">Places with differences beyond the ones in <paramref name="Hunks"/>.</param>
public sealed record ConflictComparison(ConflictComparisonKind Kind, IReadOnlyList<DiffHunk> Hunks, int MoreHunks = 0);

/// <summary>
/// The line comparison the conflict dialog shows: only the lines that differ, no context and nothing to merge; the user
/// decides with it which version to keep.
/// </summary>
public static class ConflictDiff
{
    /// <summary>Larger files are not compared (reading, decoding and the UI would take too long).</summary>
    public const long MaxFileSize = 4 * 1024 * 1024;

    /// <summary>Changed lines up to which the comparison is computed (memory grows with their square).</summary>
    public const int MaxEdits = 2000;

    /// <summary>Places shown; the rest is only counted.</summary>
    public const int MaxHunks = 200;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Compares the two versions of <paramref name="conflict"/> in the profile folder <paramref name="root"/>.</summary>
    public static async Task<ConflictComparison> CompareAsync(string root, SyncConflict conflict, CancellationToken cancellationToken)
    {
        var original = SyncPaths.Full(root, conflict.Path);
        var copy = SyncPaths.Full(root, conflict.CopyPath);
        var (localPath, remotePath) = conflict.CopyIsLocal ? (copy, original) : (original, copy);
        var local = new FileInfo(localPath);
        var remote = new FileInfo(remotePath);
        if (!local.Exists || !remote.Exists)
            return new ConflictComparison(ConflictComparisonKind.Missing, []);
        if (local.Length > MaxFileSize || remote.Length > MaxFileSize)
            return new ConflictComparison(ConflictComparisonKind.TooLarge, []);
        try
        {
            var localText = Decode(await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false));
            var remoteText = Decode(await File.ReadAllBytesAsync(remotePath, cancellationToken).ConfigureAwait(false));
            if (localText is null || remoteText is null)
                return new ConflictComparison(ConflictComparisonKind.Binary, []);
            return await Task.Run(() => Compare(localText, remoteText), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ConflictComparison(ConflictComparisonKind.Missing, []);
        }
    }

    /// <summary>The differing lines of two texts.</summary>
    public static ConflictComparison Compare(string local, string remote)
    {
        var a = SplitLines(local);
        var b = SplitLines(remote);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var x = Array.ConvertAll(a, line => Id(ids, line));
        var y = Array.ConvertAll(b, line => Id(ids, line));
        if (Changes(x, y) is not var (deleted, inserted))
            return new ConflictComparison(ConflictComparisonKind.TooManyChanges, []);

        var hunks = new List<DiffHunk>();
        var more = 0;
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && !deleted[i] && !inserted[j])
            {
                i++;
                j++;
                continue;
            }
            int localStart = i, remoteStart = j;
            while (i < a.Length && deleted[i])
                i++;
            while (j < b.Length && inserted[j])
                j++;
            if (hunks.Count == MaxHunks)
            {
                more++;
                continue;
            }
            // An empty side names the line after which the other side's lines would be.
            hunks.Add(new DiffHunk(i > localStart ? localStart + 1 : localStart, a[localStart..i],
                j > remoteStart ? remoteStart + 1 : remoteStart, b[remoteStart..j]));
        }
        var kind = hunks.Count == 0 ? ConflictComparisonKind.LineEndingsOnly : ConflictComparisonKind.Text;
        return new ConflictComparison(kind, hunks, more);
    }

    /// <summary>UTF-8 text without the byte order mark; null for anything that is not (NUL bytes, invalid sequences).</summary>
    private static string? Decode(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            span = span[3..];
        if (span.Contains((byte)0))
            return null;
        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Lines without their endings; a final line break does not start another line.</summary>
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return [];
        var lines = text.Split('\n');
        if (lines[^1].Length == 0)
            lines = lines[..^1];
        for (var k = 0; k < lines.Length; k++)
        {
            if (lines[k].EndsWith('\r'))
                lines[k] = lines[k][..^1];
        }
        return lines;
    }

    private static int Id(Dictionary<string, int> ids, string line)
    {
        if (!ids.TryGetValue(line, out var id))
            ids[line] = id = ids.Count;
        return id;
    }

    /// <summary>
    /// Which lines of <paramref name="a"/> are deleted and which of <paramref name="b"/> inserted (Myers, shortest edit
    /// script); null with more than <see cref="MaxEdits"/> changes. The common start and end are skipped first.
    /// </summary>
    private static (bool[] Deleted, bool[] Inserted)? Changes(int[] a, int[] b)
    {
        var deleted = new bool[a.Length];
        var inserted = new bool[b.Length];
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
            start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }
        int n = endA - start, m = endB - start;
        if (n + m > 0)
        {
            var max = Math.Min(n + m, MaxEdits);
            var v = new int[2 * max + 3];
            var offset = max + 1;
            var trace = new List<int[]>();
            var found = false;
            for (var d = 0; d <= max && !found; d++)
            {
                for (var k = -d; k <= d; k += 2)
                {
                    var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                    var y = x - k;
                    while (x < n && y < m && a[start + x] == b[start + y])
                    {
                        x++;
                        y++;
                    }
                    v[offset + k] = x;
                    if (x >= n && y >= m)
                        found = true;
                }
                trace.Add(v.AsSpan(offset - d, 2 * d + 1).ToArray());
            }
            if (!found)
                return null;

            int cx = n, cy = m;
            for (var d = trace.Count - 1; d > 0; d--)
            {
                var previous = trace[d - 1];
                var k = cx - cy;
                var prevK = k == -d || (k != d && previous[k - 1 + d - 1] < previous[k + 1 + d - 1]) ? k + 1 : k - 1;
                var prevX = previous[prevK + d - 1];
                var prevY = prevX - prevK;
                while (cx > prevX && cy > prevY)
                {
                    cx--;
                    cy--;
                }
                if (cx == prevX)
                    inserted[start + prevY] = true;
                else
                    deleted[start + prevX] = true;
                cx = prevX;
                cy = prevY;
            }
        }
        return (deleted, inserted);
    }
}
