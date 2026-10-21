using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class FileStabilityTracker(TimeProvider clock)
{
    private readonly Dictionary<string, (long Size, DateTime Modified, DateTimeOffset Since)> observed = new();

    public bool IsReady(FileInfo file, TimeSpan delay)
    {
        var now = clock.GetUtcNow();
        if (!observed.TryGetValue(file.FullName, out var previous) ||
            previous.Size != file.Length || previous.Modified != file.LastWriteTimeUtc)
        {
            observed[file.FullName] = (file.Length, file.LastWriteTimeUtc, now);
            return false;
        }
        return now - previous.Since >= delay;
    }

    public void Retain(ISet<string> paths)
    {
        foreach (var path in observed.Keys.Where(p => !paths.Contains(p)).ToArray()) observed.Remove(path);
    }
}

public sealed class ImportScanner(AppDbContext db, IFileHashService hashes, FileStabilityTracker stability,
    StableFileHashCache hashCache,
    IOptions<IngestionOptions> options, TimeProvider clock, ILogger<ImportScanner> logger)
{
    private const string ImportJobHashIndex = "IX_ImportJobs_FileHash";
    private const string DuplicateSourcePathIndex = "IX_DuplicateSources_ImportJobId_PathKey";

    public async Task ScanAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(options.Value.IngestPath);
        Directory.CreateDirectory(root);
        LibraryPaths.EnsureSafe(root, root);
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        }).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".epub" or ".pdf").ToArray();
        var retainedPaths = files.ToHashSet();
        stability.Retain(retainedPaths);
        hashCache.Retain(retainedPaths);
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            string? fileHash = null;
            try
            {
                LibraryPaths.EnsureSafe(root, path);
                var file = new FileInfo(path);
                if (file.Length == 0 || file.Length > options.Value.MaxFileSizeMb * 1024L * 1024 ||
                    !stability.IsReady(file, TimeSpan.FromSeconds(options.Value.FileStableSeconds))) continue;
                fileHash = await hashCache.GetHashAsync(path, hashes, ct);
                var existing = await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.FileHash == fileHash, ct);
                if (existing != null)
                {
                    await RecordDuplicateSourceAsync(existing, path, ct);
                    continue;
                }
                var now = clock.GetUtcNow().UtcDateTime;
                db.ImportJobs.Add(new ImportJob
                {
                    FilePath = path,
                    FileHash = fileHash,
                    Status = ImportStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Events = [new ImportEvent { Message = "Discovered a stable ebook file.", CreatedAt = now }]
                });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex, ImportJobHashIndex))
            {
                db.ChangeTracker.Clear();
                if (fileHash is null) throw;
                var winner = await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.FileHash == fileHash, ct);
                if (winner is null) throw;
                await RecordDuplicateSourceAsync(winner, path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not inspect an ingest file");
            }
        }
    }

    private async Task RecordDuplicateSourceAsync(ImportJob existing, string path, CancellationToken ct)
    {
        if (SourcePathIdentity.SamePath(existing.FilePath, path)) return;

        var pathKey = SourcePathIdentity.CreateKey(path);
        if (await db.DuplicateSources.AnyAsync(d => d.ImportJobId == existing.Id && d.PathKey == pathKey, ct))
            return;

        db.DuplicateSources.Add(new DuplicateSource(existing.Id, path, clock.GetUtcNow().UtcDateTime));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, DuplicateSourcePathIndex))
        {
            db.ChangeTracker.Clear();
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraint) =>
        exception.InnerException is Npgsql.PostgresException
        {
            SqlState: "23505",
            ConstraintName: var constraintName
        } && constraintName == constraint;
}
