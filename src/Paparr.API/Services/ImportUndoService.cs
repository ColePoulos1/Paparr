using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;

namespace Paparr.API.Services;

public sealed class ImportUndoService(AppDbContext db, IFileHashService hashes,
    IOptions<IngestionOptions> options, TimeProvider clock, ILogger<ImportUndoService> logger)
{
    private const int MaximumAttempts = 3;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<UndoPreviewDto> PreviewAsync(ImportJob job, CancellationToken ct)
    {
        var operation = job.CurrentUndoId is { } id
            ? await db.ImportUndos.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.ImportJobId == job.Id, ct)
            : null;
        var source = operation?.SourcePath ?? job.FilePath;
        var destination = operation?.DestinationPath ?? job.AcceptedBook?.FilePath;
        string? reason;
        var canRetry = false;
        if (job.Status == ImportStatus.Undoing && operation is not null)
        {
            reason = await ValidateCopiesAsync(source, destination, operation.FileHash, true, ct);
            canRetry = operation.Status == ImportUndoStatus.Retained && reason is null;
            reason ??= operation.Status == ImportUndoStatus.Retained
                ? "Undo needs attention. Check the retained source and retry when ready."
                : "Undo is pending. The original source will be retained.";
        }
        else if (job.Status != ImportStatus.Completed)
            reason = "Only completed imports can be undone.";
        else if (job.AcceptedBook is null || !SamePath(job.DestinationPath, destination))
            reason = "This import has no matching library record to undo.";
        else if (await db.LibraryAudits.AnyAsync(a => a.IsActive, ct))
            reason = "Wait for the active library scan to finish before undoing an import.";
        else
            reason = await ValidateCopiesAsync(source, destination, job.FileHash, false, ct);
        return new UndoPreviewDto(job.Revision, reason is null, reason,
            RelativePath(options.Value.IngestPath, source), RelativePath(options.Value.LibraryPath, destination),
            operation?.Id, operation?.Status.ToString(), operation?.Error, canRetry);
    }

    public async Task StartAsync(long jobId, Guid revision, Guid operationId, CancellationToken ct)
    {
        ImportUndo operation;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await LibraryOperationAdmission.LockAsync(db, ct);
            var existing = await db.ImportUndos.AsNoTracking().SingleOrDefaultAsync(u => u.Id == operationId, ct);
            if (existing is not null)
            {
                if (existing.ImportJobId != jobId)
                    throw new ImportConflictException("This undo operation belongs to another import. Refresh and try again.");
                operation = existing;
            }
            else
            {
                var job = await LockJobAsync(jobId, ct);
                var book = await db.Books.SingleOrDefaultAsync(b => b.ImportJobId == jobId, ct);
                if (job.Status != ImportStatus.Completed || job.Revision != revision || job.CurrentUndoId.HasValue)
                    throw new ImportConflictException("The import changed. Refresh its undo preview before continuing.");
                if (book is null || !SamePath(job.DestinationPath, book.FilePath))
                    throw new ImportConflictException("This import has no matching library record to undo.");
                if (await db.LibraryAudits.AnyAsync(a => a.IsActive, ct))
                    throw new ImportConflictException("Wait for the active library scan to finish before undoing an import.");
                var reason = await ValidateCopiesAsync(job.FilePath, book.FilePath, job.FileHash, false, ct);
                if (reason is not null) throw new ImportConflictException(reason);
                operation = new ImportUndo
                {
                    Id = operationId, ImportJobId = job.Id, BookId = book.Id,
                    SourcePath = job.FilePath, DestinationPath = book.FilePath!, FileHash = job.FileHash,
                    CreatedAt = Now, UpdatedAt = Now, Status = ImportUndoStatus.Pending, RetryAfter = Now
                };
                db.ImportUndos.Add(operation);
                job.Status = ImportStatus.Undoing;
                job.CurrentUndoId = operation.Id;
                job.CleanupStatus = SourceCleanupStatus.NotRequested;
                job.CleanupAfter = null;
                job.CleanupError = null;
                job.LastError = null;
                job.LeaseExpiresAt = null;
                Record(job, "Undo requested. The original source will be retained.");
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        if (operation.Status == ImportUndoStatus.Pending)
        {
            db.ChangeTracker.Clear();
            await ExecuteAsync(operation.Id, ct);
        }
    }

    public async Task RetryAsync(long jobId, Guid operationId, CancellationToken ct)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var job = await LockJobAsync(jobId, ct);
            var operation = await db.ImportUndos.SingleOrDefaultAsync(u => u.Id == operationId && u.ImportJobId == jobId, ct)
                ?? throw new ImportConflictException("This undo operation does not belong to the import.");
            if (operation.Status == ImportUndoStatus.Completed) return;
            if (job.Status != ImportStatus.Undoing || job.CurrentUndoId != operation.Id)
                throw new ImportConflictException("This undo is no longer active. Refresh the import.");
            if (operation.Status == ImportUndoStatus.Retained)
            {
                operation.Status = ImportUndoStatus.Pending;
                operation.AttemptCount = 0;
                operation.RetryAfter = Now;
                operation.Error = null;
                operation.UpdatedAt = Now;
                job.LastError = null;
                Record(job, "Undo retry queued. The original source will be retained.");
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        await ExecuteAsync(operationId, ct);
    }

    public async Task RunBatchAsync(CancellationToken ct)
    {
        var now = Now;
        var ids = await db.ImportUndos.AsNoTracking().Where(u => u.Status == ImportUndoStatus.Pending &&
                (u.RetryAfter == null || u.RetryAfter <= now))
            .OrderBy(u => u.CreatedAt).Select(u => u.Id).Take(20).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await ExecuteAsync(id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Undo {UndoId} will resume from its persisted state", id); }
            finally { db.ChangeTracker.Clear(); }
        }
    }

    private async Task ExecuteAsync(Guid id, CancellationToken ct)
    {
        var jobId = await db.ImportUndos.Where(u => u.Id == id).Select(u => u.ImportJobId).SingleAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockJobAsync(jobId, ct);
        var operation = await db.ImportUndos.SingleAsync(u => u.Id == id, ct);
        if (operation.Status != ImportUndoStatus.Pending || operation.RetryAfter > Now ||
            job.Status != ImportStatus.Undoing || job.CurrentUndoId != id) return;
        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == operation.BookId && b.ImportJobId == jobId, ct);
        operation.AttemptCount++;
        operation.UpdatedAt = Now;
        try
        {
            if (book is null || !SamePath(book.FilePath, operation.DestinationPath) ||
                !SamePath(job.DestinationPath, operation.DestinationPath) || !SamePath(job.FilePath, operation.SourcePath) ||
                !string.Equals(job.FileHash, operation.FileHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The undo snapshot no longer matches the import.");
            var reason = await ValidateCopiesAsync(operation.SourcePath, operation.DestinationPath, operation.FileHash, true, ct);
            if (reason is not null) throw new IOException(reason);
            ct.ThrowIfCancellationRequested();
            LibraryPaths.EnsureSafe(options.Value.LibraryPath, operation.DestinationPath);
            // Only an already durable undo may reconcile a destination deleted before a failed commit.
            try { File.Delete(operation.DestinationPath); }
            catch (DirectoryNotFoundException) { }
            db.Books.Remove(book);
            job.AcceptedBook = null;
            job.Status = ImportStatus.AwaitingApproval;
            job.CurrentUndoId = null;
            job.PublicationFence = null;
            job.SelectedCandidateId = null;
            job.DestinationPath = null;
            job.LeaseExpiresAt = null;
            job.AttemptCount = 0;
            job.LastError = null;
            job.CleanupStatus = SourceCleanupStatus.NotRequested;
            job.CleanupAttemptCount = 0;
            job.CleanupAfter = null;
            job.CleanupError = null;
            operation.Status = ImportUndoStatus.Completed;
            operation.RetryAfter = null;
            operation.Error = null;
            Record(job, "Undo completed. The library copy was removed and the retained source is ready for review.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "Undo could not finish for import {ImportId}", job.Id);
            operation.Status = operation.AttemptCount >= MaximumAttempts ? ImportUndoStatus.Retained : ImportUndoStatus.Pending;
            operation.RetryAfter = operation.Status == ImportUndoStatus.Pending ? Now.AddMinutes(operation.AttemptCount) : null;
            operation.Error = "Undo could not finish. The original source was retained. Check both copies and their permissions, then retry.";
            job.LastError = operation.Error;
            Record(job, operation.Status == ImportUndoStatus.Pending ? "Undo will be retried. The original source was retained."
                : "Undo needs attention. The original source was retained.");
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<ImportJob> LockJobAsync(long id, CancellationToken ct)
    {
        var job = await db.ImportJobs.FromSqlInterpolated($"SELECT * FROM \"ImportJobs\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleAsync(ct);
        await db.Entry(job).ReloadAsync(ct);
        return job;
    }

    private async Task<string?> ValidateCopiesAsync(string source, string? destination, string hash, bool allowMissingDestination, CancellationToken ct)
    {
        var reason = await CheckCopyAsync(options.Value.IngestPath, source, hash, "original source", false, ct);
        if (reason is not null) return reason;
        if (string.IsNullOrWhiteSpace(destination)) return "This import has no recorded library file to undo.";
        return await CheckCopyAsync(options.Value.LibraryPath, destination, hash, "library copy", allowMissingDestination, ct);
    }

    private async Task<string?> CheckCopyAsync(string root, string path, string hash, string description, bool allowMissing, CancellationToken ct)
    {
        try
        {
            LibraryPaths.EnsureSafe(root, path);
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                return $"The {description} is a directory. Restore the imported file before undoing.";
            if (new FileInfo(path).Length > options.Value.MaxFileSizeMb * 1024L * 1024)
                return $"The {description} exceeds the configured size limit. Check the file before undoing.";
            if (!string.Equals(await hashes.ComputeHashAsync(path, ct), hash, StringComparison.OrdinalIgnoreCase))
                return $"The {description} changed. Restore the imported bytes before undoing.";
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return allowMissing ? null : $"The {description} is missing. Restore it before undoing.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Could not verify the {Copy} for undo", description);
            return $"The {description} could not be verified. Check its location and permissions before undoing.";
        }
    }

    private static bool SamePath(string? first, string? second)
    {
        if (first is null || second is null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    private static string? RelativePath(string root, string? path)
    {
        if (path is null) return null;
        try
        {
            LibraryPaths.EnsureSafe(root, path);
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private void Record(ImportJob job, string message)
    {
        job.UpdatedAt = Now;
        job.Events.Add(new ImportEvent { Message = message, CreatedAt = Now });
    }
}
