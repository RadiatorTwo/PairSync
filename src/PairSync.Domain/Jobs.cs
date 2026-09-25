namespace PairSync.Domain;

public enum TransferDirection
{
    Send = 0,
    Receive = 1,
}

public enum JobState
{
    /// <summary>Created, waiting for the peer to come online.</summary>
    Waiting = 0,

    /// <summary>Offered to the receiver, waiting for accept or decline.</summary>
    AwaitingAcceptance = 1,

    Running = 2,

    Paused = 3,

    Completed = 4,

    Canceled = 5,

    Declined = 6,

    Failed = 7,
}

/// <summary>What happens when the target already has a file of the same name.</summary>
public enum ExistingFilePolicy
{
    KeepBoth = 0,
    Replace = 1,
    Skip = 2,
}

public enum JobItemState
{
    Pending = 0,
    Transferring = 1,
    Completed = 2,
    Skipped = 3,
    Failed = 4,
}

/// <summary>One send or receive order with a peer: files and folders flattened into <see cref="Items"/>.</summary>
public sealed class TransferJob
{
    public Guid Id { get; set; }

    public Guid PeerDeviceId { get; set; }

    public TransferDirection Direction { get; set; }

    public JobState State { get; set; }

    public ExistingFilePolicy Policy { get; set; }

    /// <summary>Sender: proposed target folder name. Receiver: the confirmed absolute target folder.</summary>
    public string? TargetPath { get; set; }

    public long TotalBytes { get; set; }

    public int FileCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// Paused because the other device asked for it (its user, or its disk is full). Such a pause ends when the other
    /// device resumes; a pause by the local user only ends when the local user resumes.
    /// </summary>
    public bool PausedByPeer { get; set; }

    /// <summary>For lists: the first top-level file or folder, "+n" for more, e.g. <c>Photos-2026 +2</c>.</summary>
    public string Title { get; set; } = "";

    public List<JobItem> Items { get; set; } = [];

    public bool IsFinished => State is JobState.Completed or JobState.Canceled or JobState.Declined or JobState.Failed;
}

public static class JobTitles
{
    /// <summary>First top-level name of the relative paths, with "+n" for further top-level entries.</summary>
    public static string From(IEnumerable<string> relativePaths)
    {
        var tops = relativePaths.Select(p => p.Split('/', 2)[0]).Distinct(StringComparer.Ordinal).ToList();
        return tops.Count switch
        {
            0 => "",
            1 => tops[0],
            _ => $"{tops[0]} +{tops.Count - 1}",
        };
    }
}

/// <summary>One file of a job, addressed by a normalized relative path.</summary>
public sealed class JobItem
{
    public long Id { get; set; }

    public Guid JobId { get; set; }

    /// <summary>Forward-slash separated, relative to the job root.</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>Sender side only: where the file is read from.</summary>
    public string? SourcePath { get; set; }

    public long Size { get; set; }

    public DateTime LastWriteTimeUtc { get; set; }

    /// <summary>Identifies the chunk transfer of this file version on the wire and in the chunk journal.</summary>
    public Guid TransferId { get; set; }

    public JobItemState State { get; set; }

    /// <summary>An empty folder: created on the receiver, nothing to transfer.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>Receiver side: where the file ended up (differs from the relative path with "Keep both").</summary>
    public string? ResultPath { get; set; }

    /// <summary>Why the item failed or was skipped.</summary>
    public string? Message { get; set; }
}
