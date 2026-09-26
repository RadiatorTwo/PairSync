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

    public DbSet<SyncProfile> SyncProfiles => Set<SyncProfile>();

    public DbSet<SyncFile> SyncFiles => Set<SyncFile>();

    public DbSet<SyncConflict> SyncConflicts => Set<SyncConflict>();

    public DbSet<SyncActivity> SyncActivities => Set<SyncActivity>();

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

        modelBuilder.Entity<SyncProfile>(profile =>
        {
            profile.ToTable("SyncProfiles");
            profile.HasKey(p => p.Id);
            profile.Property(p => p.Name).HasMaxLength(256);
            profile.HasIndex(p => p.PeerDeviceId);
            // No foreign key to Devices: removing a device detaches its profiles, the files stay.
            profile.Ignore(p => p.TakesChanges);
            profile.Ignore(p => p.DeliversFiles);
        });

        modelBuilder.Entity<SyncFile>(file =>
        {
            file.ToTable("SyncFiles");
            file.HasKey(f => new { f.ProfileId, f.Side, f.Path });
            file.HasIndex(f => new { f.ProfileId, f.Side, f.Sequence });
            file.HasOne<SyncProfile>().WithMany().HasForeignKey(f => f.ProfileId).OnDelete(DeleteBehavior.Cascade);
            file.Ignore(f => f.VersionVector);
        });

        modelBuilder.Entity<SyncConflict>(conflict =>
        {
            conflict.ToTable("SyncConflicts");
            conflict.HasKey(c => c.Id);
            conflict.HasIndex(c => new { c.ProfileId, c.Resolved });
            conflict.HasOne<SyncProfile>().WithMany().HasForeignKey(c => c.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SyncActivity>(activity =>
        {
            activity.ToTable("SyncActivities");
            activity.HasKey(a => a.Id);
            activity.HasIndex(a => new { a.ProfileId, a.Id });
            activity.HasOne<SyncProfile>().WithMany().HasForeignKey(a => a.ProfileId).OnDelete(DeleteBehavior.Cascade);
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
