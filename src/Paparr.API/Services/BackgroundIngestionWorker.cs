using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class BackgroundIngestionWorker(IServiceScopeFactory scopes, IOptions<IngestionOptions> options,
    TimeProvider clock, ILogger<BackgroundIngestionWorker> logger, WorkerDiagnostics diagnostics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), clock);
        do
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ingestion cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        diagnostics.Start();
        await RunIndependentStageAsync(WorkerStage.QueuedWork, ct, () =>
            RunBatchesAsync(RunUndoAsync, RunQueuedWorkAsync, ct));
        await RunIndependentStageAsync(WorkerStage.SourceCleanup, ct, () =>
            RunBatchesAsync(RunStagingCleanupAsync, RunSourceCleanupAsync, ct));
        await RunIndependentStageAsync(WorkerStage.LibraryAudit, ct, () => RunLibraryAuditAsync(ct));
        await diagnostics.RunStageAsync(WorkerStage.Discovery, ct, () => RunDiscoveryAsync(ct));
    }

    private async Task RunIndependentStageAsync(WorkerStage stage, CancellationToken ct, Func<Task> action)
    {
        try
        {
            await diagnostics.RunStageAsync(stage, ct, action);
        }
        catch (DbUpdateConcurrencyException) when (stage == WorkerStage.LibraryAudit)
        {
            logger.LogDebug("Library scan was claimed by another worker");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogError(ex, "Worker stage {Stage} failed", stage); }
    }

    private static async Task RunBatchesAsync(Func<CancellationToken, Task> first,
        Func<CancellationToken, Task> next, CancellationToken ct)
    {
        try { await first(ct); }
        finally
        {
            ct.ThrowIfCancellationRequested();
            await next(ct);
        }
    }

    private async Task RunUndoAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ImportUndoService>().RunBatchAsync(ct);
    }

    private async Task RunQueuedWorkAsync(CancellationToken ct)
    {
        // Queue progress must not depend on the watch directory being available.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;
            var ids = await db.ImportJobs.AsNoTracking().Where(j => j.Status == ImportStatus.Pending ||
                ((j.Status == ImportStatus.Processing || j.Status == ImportStatus.Publishing) &&
                 (j.LeaseExpiresAt == null || j.LeaseExpiresAt <= now)))
                .OrderBy(j => j.CreatedAt).Select(j => j.Id).Take(20).ToListAsync(ct);
            foreach (var id in ids)
            {
                using var jobScope = scopes.CreateScope();
                var jobDb = jobScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var job = await jobDb.ImportJobs.Include(j => j.Candidates).FirstAsync(j => j.Id == id, ct);
                var ingestion = jobScope.ServiceProvider.GetRequiredService<IEbookIngestionService>();
                try
                {
                    if (job.Status == ImportStatus.Pending) await ingestion.ProcessImportJobAsync(job, ct);
                    else await ingestion.RecoverAsync(job, ct);
                }
                catch (DbUpdateConcurrencyException) { logger.LogDebug("Import {ImportId} was claimed by another request", id); }
                catch (ImportConflictException ex) { logger.LogWarning("Import {ImportId}: {Reason}", id, ex.Message); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError(ex, "Import {ImportId} failed; persisted work will be recovered", id); }
            }
        }
    }

    private async Task RunStagingCleanupAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StagingService>().RunBatchAsync(ct);
    }

    private async Task RunSourceCleanupAsync(CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;
            var ids = await db.ImportJobs.AsNoTracking().Where(j => j.Status == ImportStatus.Completed &&
                j.CleanupStatus == SourceCleanupStatus.Pending && (j.CleanupAfter == null || j.CleanupAfter <= now))
                .OrderBy(j => j.CleanupAfter).Select(j => j.Id).Take(20).ToListAsync(ct);
            foreach (var id in ids)
            {
                using var cleanupScope = scopes.CreateScope();
                var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var job = await cleanupDb.ImportJobs.SingleAsync(j => j.Id == id, ct);
                try { await cleanupScope.ServiceProvider.GetRequiredService<SourceCleanupService>().TryCleanupAsync(job, ct); }
                catch (DbUpdateConcurrencyException) { logger.LogDebug("Source cleanup {ImportId} was claimed elsewhere", id); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError(ex, "Source cleanup {ImportId} will be recovered", id); }
            }
        }
    }

    private async Task RunLibraryAuditAsync(CancellationToken ct)
    {
        using var scanScope = scopes.CreateScope();
        await scanScope.ServiceProvider.GetRequiredService<LibraryAuditService>().RunBatchAsync(ct);
    }

    private async Task RunDiscoveryAsync(CancellationToken ct)
    {
        using var discoveryScope = scopes.CreateScope();
        await discoveryScope.ServiceProvider.GetRequiredService<ImportScanner>().ScanAsync(ct);
    }
}
