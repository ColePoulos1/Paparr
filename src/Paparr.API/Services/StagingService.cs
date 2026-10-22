using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class StagingService(AppDbContext db, ILibraryPublisher publisher,
    IOptions<IngestionOptions> options, TimeProvider clock, ILogger<StagingService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<PublicationStage> PrepareAsync(ImportJob job, Guid fence, CancellationToken ct)
    {
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, job.DestinationPath!);
        var id = Guid.NewGuid();
        var stage = new PublicationStage
        {
            Id = id,
            ImportJobId = job.Id,
            PublicationFence = fence,
            FilePath = job.DestinationPath + "." + id.ToString("N") + ".partial",
            DestinationPath = job.DestinationPath!,
            FileHash = job.FileHash,
            Status = PublicationStageStatus.Planned,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.PublicationStages.Add(stage);
        await db.SaveChangesAsync(ct);
        await publisher.PrepareAsync(job.FilePath, stage.DestinationPath, stage.FilePath, stage.FileHash,
            async token =>
            {
                stage.Status = PublicationStageStatus.Owned;
                stage.UpdatedAt = Now;
                await db.SaveChangesAsync(token);
            }, ct);
        return stage;
    }

    public async Task RunBatchAsync(CancellationToken ct)
    {
        var now = Now;
        var ids = await db.PublicationStages.AsNoTracking()
            .Where(s => (s.Status == PublicationStageStatus.Planned || s.Status == PublicationStageStatus.Owned ||
                s.Status == PublicationStageStatus.Published) && (s.CleanupAfter == null || s.CleanupAfter <= now))
            .Where(s => !db.ImportJobs.Any(j => j.Id == s.ImportJobId && j.Status == ImportStatus.Publishing &&
                j.PublicationFence == s.PublicationFence))
            .OrderBy(s => s.CreatedAt).Select(s => s.Id).Take(20).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await CleanupAsync(id, ct); }
            catch (DbUpdateConcurrencyException) { logger.LogDebug("Staging cleanup was claimed elsewhere"); }
            finally { db.ChangeTracker.Clear(); }
        }
    }

    private async Task CleanupAsync(Guid id, CancellationToken ct)
    {
        var jobId = await db.PublicationStages.Where(s => s.Id == id).Select(s => s.ImportJobId).SingleAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.ImportJobs.FromSqlInterpolated($"SELECT * FROM \"ImportJobs\" WHERE \"Id\" = {jobId} FOR UPDATE")
            .AsNoTracking().SingleAsync(ct);
        var stage = await db.PublicationStages.SingleAsync(s => s.Id == id, ct);
        if (stage.Status is PublicationStageStatus.Cleaned or PublicationStageStatus.Retained || stage.CleanupAfter > Now ||
            (job.Status == ImportStatus.Publishing && job.PublicationFence == stage.PublicationFence)) return;

        stage.CleanupAttemptCount++;
        stage.UpdatedAt = Now;
        try
        {
            Validate(stage);
            if (!publisher.StageExists(stage.FilePath))
                stage.Status = PublicationStageStatus.Cleaned;
            else if (stage.Status == PublicationStageStatus.Planned)
            {
                // A planned row does not prove that CreateNew succeeded before an interruption.
                stage.Status = PublicationStageStatus.Retained;
                stage.CleanupError = "Staging ownership was not confirmed. The file was retained.";
                logger.LogWarning("Temporary file for import {ImportId}, stage {StageId} was retained because creation ownership was not confirmed",
                    stage.ImportJobId, stage.Id);
            }
            else
            {
                publisher.DeleteStage(stage.FilePath);
                stage.Status = PublicationStageStatus.Cleaned;
            }
            stage.CleanupAfter = null;
            if (stage.Status == PublicationStageStatus.Cleaned) stage.CleanupError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "Staging cleanup failed for import {ImportId}", stage.ImportJobId);
            stage.CleanupAfter = Now.AddMinutes(Math.Min(stage.CleanupAttemptCount, 60));
            stage.CleanupError = "The staging file was retained. Check library paths and permissions; cleanup will be retried.";
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private void Validate(PublicationStage stage)
    {
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, stage.DestinationPath);
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, stage.FilePath);
        var expected = stage.DestinationPath + "." + stage.Id.ToString("N") + ".partial";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(stage.FilePath).Equals(Path.GetFullPath(expected), comparison))
            throw new IOException("The staging path does not match its ownership record.");
    }
}
