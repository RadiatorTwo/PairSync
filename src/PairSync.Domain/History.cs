namespace PairSync.Domain;

public enum HistoryOutcome
{
    Completed = 0,
    Canceled = 1,
    Declined = 2,
    Failed = 3,
}

/// <summary>A finished job as shown under "Recently completed". Kept after the job and even the device are gone.</summary>
public sealed class HistoryEntry
{
    public long Id { get; set; }

    public Guid JobId { get; set; }

    public Guid PeerDeviceId { get; set; }

    /// <summary>Name of the peer at the time, so the entry still reads well after the device is removed.</summary>
    public string PeerName { get; set; } = "";

    public TransferDirection Direction { get; set; }

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime FinishedAtUtc { get; set; }

    public HistoryOutcome Outcome { get; set; }

    public string? Message { get; set; }
}
