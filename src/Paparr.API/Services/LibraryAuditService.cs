using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class LibraryAuditService(AppDbContext db, IFileHashService hashes,
    IOptions<IngestionOptions> options, TimeProvider clock, ILogger<LibraryAuditService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<LibraryAudit> QueueAsync(CancellationToken ct)
    {
        var active = await db.LibraryAudits.FirstOrDefaultAsync(a => a.IsActive, ct);
        if (active is not null) return active;
        var maximum = await db.Books.Select(b => (long?)b.Id).MaxAsync(ct) ?? 0;
        var audit = new LibraryAudit
        {
            CreatedAt = Now,
            MaximumBookId = maximum,
            TotalBooks = await db.Books.CountAsync(b => b.Id <= maximum, ct)
        };
        db.LibraryAudits.Add(audit);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return await db.LibraryAudits.FirstOrDefaultAsync(a => a.IsActive, ct)
                ?? throw new ImportConflictException("The previous scan just finished. Start another scan to check again.");
        }
        return audit;
    }

    public async Task RunBatchAsync(CancellationToken ct)
    {
        var audit = await db.LibraryAudits.SingleOrDefaultAsync(a => a.IsActive, ct);
        if (audit is null || audit.LeaseExpiresAt > Now) return;
        audit.Status = AuditStatus.Running;
        audit.LeaseExpiresAt = Now.AddMinutes(options.Value.LeaseMinutes);
        await db.SaveChangesAsync(ct);

        var books = await db.Books.Include(b => b.ImportJob)
            .Where(b => b.Id > audit.LastBookId && b.Id <= audit.MaximumBookId)
            .OrderBy(b => b.Id).Take(20).ToListAsync(ct);
        foreach (var book in books)
        {
            var result = await CheckAsync(book, ct);
            book.IntegrityStatus = result.Status;
            book.IntegrityMessage = result.Message;
            book.IntegrityCheckedAt = Now;
            audit.ProcessedBooks++;
            switch (result.Status)
            {
                case IntegrityStatus.Verified: audit.VerifiedBooks++; break;
                case IntegrityStatus.Missing: audit.MissingBooks++; break;
                case IntegrityStatus.Changed: audit.ChangedBooks++; break;
                default: audit.UnavailableBooks++; break;
            }
            audit.LastBookId = book.Id;
            audit.LeaseExpiresAt = Now.AddMinutes(options.Value.LeaseMinutes);
            // Persist each result and its cursor together so interrupted scans do not double-count books.
            await db.SaveChangesAsync(ct);
        }

        if (books.Count < 20 || audit.LastBookId >= audit.MaximumBookId)
        {
            audit.Status = AuditStatus.Completed;
            audit.IsActive = false;
            audit.CompletedAt = Now;
            audit.LeaseExpiresAt = null;
        }
        else audit.LeaseExpiresAt = Now;
        await db.SaveChangesAsync(ct);
    }

    private async Task<(IntegrityStatus Status, string? Message)> CheckAsync(Book book, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(book.FilePath) || book.ImportJob?.FileHash is not { Length: 64 } expected)
            return (IntegrityStatus.Unavailable, "This book has no recorded file or original hash.");
        try
        {
            LibraryPaths.EnsureSafe(options.Value.LibraryPath, book.FilePath);
            if (File.GetAttributes(book.FilePath).HasFlag(FileAttributes.Directory))
                return (IntegrityStatus.Unavailable, "The library path is a directory instead of a file. Check the configured library directory.");
            var info = new FileInfo(book.FilePath);
            if (info.Length > options.Value.MaxFileSizeMb * 1024L * 1024)
                return (IntegrityStatus.Unavailable, "The file exceeds the configured scan size limit.");
            var actual = await hashes.ComputeHashAsync(book.FilePath, ct);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                ? (IntegrityStatus.Verified, null)
                : (IntegrityStatus.Changed, "The library file differs from the imported copy. Check your backup before replacing it.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (IntegrityStatus.Missing, "The library file disappeared during the check.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "Integrity check could not read book {BookId}", book.Id);
            return (IntegrityStatus.Unavailable, "The file could not be checked. Check permissions and the configured library directory.");
        }
    }
}
