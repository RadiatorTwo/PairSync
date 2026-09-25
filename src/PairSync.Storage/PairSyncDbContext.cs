using Microsoft.EntityFrameworkCore;
using PairSync.Domain;

namespace PairSync.Storage;

public sealed class PairSyncDbContext(DbContextOptions<PairSyncDbContext> options) : DbContext(options)
{
    public DbSet<PairedDevice> Devices => Set<PairedDevice>();

    public DbSet<TransferJob> Jobs => Set<TransferJob>();

    public DbSet<JobItem> JobItems => Set<JobItem>();

    public DbSet<ChunkJournalRecord> ChunkJournal => Set<ChunkJournalRecord>();

    public DbSet<HistoryEntry> History => Set<HistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PairedDevice>(device =>
        {
            device.ToTable("Devices");
            device.HasKey(d => d.Id);
            device.Property(d => d.Name).HasMaxLength(256);
            device.Property(d => d.PublicKey).IsRequired();
            device.HasIndex(d => d.PublicKey).IsUnique();
        });

        modelBuilder.Entity<TransferJob>(job =>
        {
            job.ToTable("Jobs");
            job.HasKey(j => j.Id);
            job.HasIndex(j => new { j.State, j.PeerDeviceId });
            // No foreign key to Devices: removing a device must not silently delete its jobs.
            job.HasMany(j => j.Items).WithOne().HasForeignKey(i => i.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobItem>(item =>
        {
            item.ToTable("JobItems");
            item.HasKey(i => i.Id);
            item.HasIndex(i => new { i.JobId, i.RelativePath }).IsUnique();
            item.HasIndex(i => i.TransferId);
        });

        modelBuilder.Entity<ChunkJournalRecord>(journal =>
        {
            journal.ToTable("ChunkJournal");
            journal.HasKey(j => j.TransferId);
            journal.Property(j => j.Confirmed).IsRequired();
        });

        modelBuilder.Entity<HistoryEntry>(entry =>
        {
            entry.ToTable("History");
            entry.HasKey(h => h.Id);
            entry.HasIndex(h => h.FinishedAtUtc);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date type; UTC ticks keep ordering and equality exact.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcTicksConverter>();
    }
}

/// <summary>Row of the receiver-side chunk journal, see <see cref="SyncEngine.ChunkJournalEntry"/>.</summary>
public sealed class ChunkJournalRecord
{
    public Guid TransferId { get; set; }

    public string TempPath { get; set; } = "";

    public long FileSize { get; set; }

    public int ChunkSize { get; set; }

    public int ChunkCount { get; set; }

    public DateTime LastWriteTimeUtc { get; set; }

    public byte[] Confirmed { get; set; } = [];

    public DateTime UpdatedAtUtc { get; set; }
}
