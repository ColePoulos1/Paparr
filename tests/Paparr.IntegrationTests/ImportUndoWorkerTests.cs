using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class ImportUndoWorkerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_replayable_undo_commit_failure_does_not_block_unrelated_queued_imports_or_discovery()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        using (var client = app.CreateClient())
        using (var approved = await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null))
            approved.EnsureSuccessStatusCode();
        var imported = await app.ReadAsync(id);
        var operationId = Guid.NewGuid();
        var failure = new PersistentCompletionFailure(operationId);
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(failure))));
        using (var client = host.CreateClient())
        using (var interrupted = await client.PostAsJsonAsync($"/api/imports/{id}/undo",
            new { revision = imported.Revision, operationId }))
            Assert.Equal(HttpStatusCode.InternalServerError, interrupted.StatusCode);
        Assert.Equal(ImportStatus.Undoing, (await app.ReadAsync(id)).Status);
        Assert.False(File.Exists(imported.DestinationPath));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var operation = await db.ImportUndos.SingleAsync(u => u.Id == operationId);
            operation.RetryAfter = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        var independent = await app.SeedAsync(ImportStatus.Pending);
        var worker = ActivatorUtilities.CreateInstance<BackgroundIngestionWorker>(host.Services);

        await worker.RunCycleAsync(default);

        Assert.Equal(2, failure.Count);
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(independent)).Status);
        Assert.Equal(ImportStatus.Undoing, (await app.ReadAsync(id)).Status);
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(imported.FilePath));
        Assert.False(File.Exists(imported.DestinationPath));
        var stages = host.Services.GetRequiredService<WorkerDiagnostics>().Snapshot().Stages;
        Assert.Equal("Succeeded", stages.Single(s => s.Key == "sourceCleanup").State);
        Assert.Equal("Succeeded", stages.Single(s => s.Key == "discovery").State);
        using var verify = host.Services.CreateScope();
        var retained = await verify.ServiceProvider.GetRequiredService<AppDbContext>().ImportUndos.SingleAsync();
        Assert.Equal(ImportUndoStatus.Pending, retained.Status);
    }

    private sealed class PersistentCompletionFailure(Guid operationId) : SaveChangesInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ImportUndo>().Any(e => e.State == EntityState.Modified &&
                e.Entity.Id == operationId && e.Entity.Status == ImportUndoStatus.Completed))
            {
                Count++;
                throw new DbUpdateException("Injected persistent undo completion failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
