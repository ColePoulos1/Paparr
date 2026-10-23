using Microsoft.EntityFrameworkCore;
using Paparr.API.Domain;

namespace Paparr.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<ImportJob> ImportJobs { get; set; } = null!;
    public DbSet<Book> Books { get; set; } = null!;
    public DbSet<MetadataCandidate> MetadataCandidates { get; set; } = null!;
    public DbSet<ImportEvent> ImportEvents { get; set; } = null!;
    public DbSet<LibraryAudit> LibraryAudits { get; set; } = null!;
    public DbSet<AuthSession> AuthSessions { get; set; } = null!;
    public DbSet<DuplicateSource> DuplicateSources { get; set; } = null!;
    public DbSet<PublicationStage> PublicationStages { get; set; } = null!;
    public DbSet<ImportUndo> ImportUndos { get; set; } = null!;

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<ImportJob>().Where(e => e.State == EntityState.Modified))
            entry.Entity.Revision = Guid.NewGuid();
        foreach (var entry in ChangeTracker.Entries<LibraryAudit>().Where(e => e.State == EntityState.Modified))
            entry.Entity.Revision = Guid.NewGuid();
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<ImportJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired();
            entity.Property(e => e.FileHash).IsRequired();
            entity.Property(e => e.ProfileName).HasMaxLength(100);
            entity.Property(e => e.ProfileIngestSubfolder).HasMaxLength(1024);
            entity.Property(e => e.CollectionSegment).HasMaxLength(70);
            entity.Property(e => e.FilenameOrder).HasConversion<string>().HasMaxLength(20).HasDefaultValue(FilenameOrder.TitleAuthor);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.CleanupStatus).HasConversion<string>();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasIndex(e => e.FileHash).IsUnique();
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.CleanupStatus, e.CleanupAfter });
            entity.HasMany(e => e.Events).WithOne().HasForeignKey(e => e.ImportJobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Candidates)
                .WithOne(c => c.ImportJob)
                .HasForeignKey(c => c.ImportJobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.DuplicateSources)
                .WithOne(d => d.ImportJob)
                .HasForeignKey(d => d.ImportJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DuplicateSource>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired();
            entity.Property(e => e.PathKey).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.ImportJobId, e.PathKey }).IsUnique();
            entity.HasIndex(e => new { e.ImportJobId, e.DiscoveredAt, e.Id });
        });

        modelBuilder.Entity<PublicationStage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.HasIndex(e => e.FilePath).IsUnique();
            entity.HasIndex(e => new { e.Status, e.CleanupAfter });
            entity.HasOne<ImportJob>().WithMany().HasForeignKey(e => e.ImportJobId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportUndo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.HasIndex(e => new { e.Status, e.RetryAfter });
            entity.HasIndex(e => new { e.ImportJobId, e.CreatedAt });
            entity.HasOne<ImportJob>().WithMany().HasForeignKey(e => e.ImportJobId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Book>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired();
            entity.Property(e => e.Author).IsRequired();
            entity.Property(e => e.Source).IsRequired();
            entity.Property(e => e.ExternalId).IsRequired();
            entity.Property(e => e.IntegrityStatus).HasConversion<string>();
            entity.Property(e => e.Isbn13).HasMaxLength(13);
            entity.Property(e => e.IsbnStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Publisher).HasMaxLength(300);
            entity.Property(e => e.Language).HasMaxLength(35);
            entity.HasIndex(e => e.IntegrityStatus);

            entity.HasOne(e => e.ImportJob)
                .WithOne(j => j.AcceptedBook)
                .HasForeignKey<Book>(b => b.ImportJobId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MetadataCandidate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired();
            entity.Property(e => e.Author).IsRequired();
            entity.Property(e => e.Source).IsRequired();
            entity.Property(e => e.ExternalId).IsRequired();
            entity.Property(e => e.Isbn13).HasMaxLength(13);
            entity.Property(e => e.IsbnStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Publisher).HasMaxLength(300);
            entity.Property(e => e.Language).HasMaxLength(35);

            entity.HasOne(c => c.ImportJob)
                .WithMany(j => j.Candidates)
                .HasForeignKey(c => c.ImportJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LibraryAudit>(entity =>
        {
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasIndex(e => e.IsActive).IsUnique().HasFilter("\"IsActive\" = TRUE");
        });
    }
}
