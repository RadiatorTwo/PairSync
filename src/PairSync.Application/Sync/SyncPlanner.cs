using System.Globalization;
using PairSync.Domain;

namespace PairSync.Application.Sync;

public enum SyncActionKind
{
    None,

    /// <summary>Fetch the other device's file into place (the local one, if any, goes to the trash).</summary>
    Fetch,

    /// <summary>Create the folder the other device has.</summary>
    CreateDirectory,

    /// <summary>Apply the other device's deletion: the local file or empty folder goes to the trash.</summary>
    Delete,

    /// <summary>Same content on both sides: only the version is taken over.</summary>
    AdoptVersion,

    /// <summary>Deleted there, changed here: the local version stays and is announced as newer.</summary>
    KeepLocal,

    /// <summary>Both sides changed the same version differently; both versions are kept.</summary>
    Conflict,
}

/// <summary>What to do with one path.</summary>
/// <param name="Version">The local version after the action (<see cref="VersionVector"/> text form).</param>
/// <param name="Note">Worth telling the user, e.g. that a deletion was undone.</param>
public sealed record SyncAction(SyncActionKind Kind, string Path, SyncFile? Local, SyncFile? Remote, string Version, string? Note = null)
{
    public static SyncAction Nothing(string path) => new(SyncActionKind.None, path, null, null, "");
}

/// <summary>
/// Decides per path what a device does with the other device's entry (phase 3 block C, plan §8). Only versions
/// decide: an entry that follows from the local one is taken over, one the local entry follows from is ignored,
/// and two independent changes are a conflict unless they have the same content. Clocks never pick a winner.
/// </summary>
public static class SyncPlanner
{
    public static SyncAction Plan(SyncProfile profile, Guid localDevice, SyncFile? local, SyncFile? remote)
    {
        var path = remote?.Path ?? local?.Path ?? "";
        if (remote is null || !profile.TakesChanges)
            return SyncAction.Nothing(path);

        var remoteVersion = remote.VersionVector;
        if (local is null)
        {
            if (remote.Deleted)
                return SyncAction.Nothing(path);
            return profile.AllowWrite
                ? new SyncAction(remote.IsDirectory ? SyncActionKind.CreateDirectory : SyncActionKind.Fetch, path, null, remote, remote.Version)
                : SyncAction.Nothing(path);
        }

        var localVersion = local.VersionVector;
        var merged = localVersion.Merge(remoteVersion).ToString();
        switch (remoteVersion.Compare(localVersion))
        {
            case VersionOrder.Equal or VersionOrder.Older:
                return SyncAction.Nothing(path);

            case VersionOrder.Newer:
                if (local.SameContentAs(remote))
                    return new SyncAction(SyncActionKind.AdoptVersion, path, local, remote, merged);
                if (remote.Deleted)
                {
                    if (local.Deleted || !profile.AllowDelete)
                        return SyncAction.Nothing(path);
                    return new SyncAction(SyncActionKind.Delete, path, local, remote, merged);
                }
                return profile.AllowWrite
                    ? new SyncAction(remote.IsDirectory ? SyncActionKind.CreateDirectory : SyncActionKind.Fetch, path, local, remote, merged)
                    : SyncAction.Nothing(path);

            default:
                if (local.SameContentAs(remote))
                    return new SyncAction(SyncActionKind.AdoptVersion, path, local, remote, merged);
                if (remote.Deleted)
                {
                    // Deleted there, changed here: the change wins and comes back to the other device.
                    return new SyncAction(SyncActionKind.KeepLocal, path, local, remote, localVersion.Merge(remoteVersion).Increment(localDevice).ToString(),
                        $"{path} was deleted on the other device but changed here; it was kept.");
                }
                if (local.Deleted)
                {
                    if (!profile.AllowWrite)
                        return SyncAction.Nothing(path);
                    return new SyncAction(remote.IsDirectory ? SyncActionKind.CreateDirectory : SyncActionKind.Fetch, path, local, remote, merged,
                        $"{path} was deleted here but changed on the other device; it was restored.");
                }
                if (local.IsDirectory && remote.IsDirectory)
                    return new SyncAction(SyncActionKind.AdoptVersion, path, local, remote, merged);
                return profile.AllowWrite
                    ? new SyncAction(SyncActionKind.Conflict, path, local, remote, merged)
                    : SyncAction.Nothing(path);
        }
    }
}

/// <summary>
/// Conflict copies (plan §8): the device with the smaller id moves its version aside, the other keeps the original
/// path. The name uses only what both devices know, so each computes the same copy path without asking the other.
/// </summary>
public static class ConflictNames
{
    /// <summary>True if <paramref name="localDevice"/> is the one that renames its version.</summary>
    public static bool LocalRenames(Guid localDevice, Guid remoteDevice) =>
        string.CompareOrdinal(localDevice.ToString("N"), remoteDevice.ToString("N")) < 0;

    /// <summary><c>docs/plan (conflict 3F2A91 2026-09-26 154210).md</c>: id and modification time of the renamed version.</summary>
    public static string CopyPath(string path, Guid renamingDevice, DateTime renamedVersionMTimeUtc)
    {
        var slash = path.LastIndexOf('/');
        var (folder, name) = slash < 0 ? ("", path) : (path[..(slash + 1)], path[(slash + 1)..]);
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        var id = renamingDevice.ToString("N")[..6].ToUpperInvariant();
        var stamp = renamedVersionMTimeUtc.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        return $"{folder}{stem} (conflict {id} {stamp}){extension}";
    }
}
