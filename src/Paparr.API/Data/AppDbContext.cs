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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ImportJob configuration
        modelBuilder.Entity<ImportJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired();
            entity.Property(e => e.FileHash).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>();
            
            entity.HasMany(e => e.Candidates)
                .WithOne(c => c.ImportJob)
                .HasForeignKey(c => c.ImportJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Book configuration
        modelBuilder.Entity<Book>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired();
            entity.Property(e => e.Author).IsRequired();
            entity.Property(e => e.Source).IsRequired();
            entity.Property(e => e.ExternalId).IsRequired();
            
            entity.HasOne(e => e.ImportJob)
                .WithOne(j => j.AcceptedBook)
                .HasForeignKey<Book>(b => b.ImportJobId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // MetadataCandidate configuration
        modelBuilder.Entity<MetadataCandidate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired();
            entity.Property(e => e.Author).IsRequired();
            entity.Property(e => e.Source).IsRequired();
            entity.Property(e => e.ExternalId).IsRequired();
            
            entity.HasOne(c => c.ImportJob)
                .WithMany(j => j.Candidates)
                .HasForeignKey(c => c.ImportJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
