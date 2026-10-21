using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class SourceCleanupService(AppDbContext db, ILibraryPublisher publisher,
    IOptions<IngestionOptions> options, TimeProvider clock, ILogger<SourceCleanupService> logger)
{
    private const int MaximumAttempts = 3;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task TryCleanupAsync(ImportJob job, CancellationToken ct)
    {
        if (job.Status != ImportStatus.Completed || job.CleanupStatus != SourceCleanupStatus.Pending ||
            job.CleanupAfter > Now) return;
        if (!options.Value.DeleteSourceAfterImport)
        {
            job.CleanupStatus = SourceCleanupStatus.Retained;
            job.CleanupAfter = null;
            job.CleanupError = "Automatic source cleanup is disabled. The source was retained.";
            Record(job, job.CleanupError);
            await db.SaveChangesAsync(ct);
            return;
        }

        job.CleanupAttemptCount++;
        job.CleanupAfter = Now.AddMinutes(options.Value.LeaseMinutes);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return; }

        try
        {
            await publisher.CleanupSourceAsync(job.FilePath, job.DestinationPath!, job.FileHash, ct);
            job.CleanupStatus = SourceCleanupStatus.Completed;
            job.CleanupAfter = null;
            job.CleanupError = null;
            Record(job, "Source cleanup completed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Source cleanup failed for import {ImportId}", job.Id);
            job.CleanupStatus = job.CleanupAttemptCount >= MaximumAttempts
                ? SourceCleanupStatus.Retained : SourceCleanupStatus.Pending;
            job.CleanupAfter = job.CleanupStatus == SourceCleanupStatus.Pending
                ? Now.AddMinutes(job.CleanupAttemptCount) : null;
            job.CleanupError = "The source was retained. Check file permissions and verify both copies still match before retrying cleanup.";
            Record(job, job.CleanupStatus == SourceCleanupStatus.Pending
                ? "Source cleanup will be retried." : "Source cleanup needs attention. The library import is complete.");
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task QueueRetryAsync(ImportJob job, CancellationToken ct)
    {
        if (job.Status != ImportStatus.Completed || job.CleanupStatus == SourceCleanupStatus.NotRequested)
            throw new ImportConflictException("Source cleanup was not requested for this import.");
        if (job.CleanupStatus is SourceCleanupStatus.Pending or SourceCleanupStatus.Completed) return;
        if (!options.Value.DeleteSourceAfterImport)
            throw new ImportConflictException("Enable DeleteSourceAfterImport before retrying source cleanup.");
        job.CleanupStatus = SourceCleanupStatus.Pending;
        job.CleanupAttemptCount = 0;
        job.CleanupAfter = Now;
        job.CleanupError = null;
        Record(job, "Source cleanup retry queued.");
        await db.SaveChangesAsync(ct);
    }

    private void Record(ImportJob job, string message)
    {
        job.UpdatedAt = Now;
        job.Events.Add(new ImportEvent { Message = message, CreatedAt = Now });
    }
}
