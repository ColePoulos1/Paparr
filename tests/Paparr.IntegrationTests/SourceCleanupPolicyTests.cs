using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class SourceCleanupPolicyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Restart_with_cleanup_disabled_retains_a_persisted_pending_source()
    {
        using var original = await PaparrFactory.CreateAsync(postgres);
        using var client = original.CreateClient();
        var (id, source, destination) = await CompleteWithPendingCleanupAsync(original, client);

        using var restarted = new PaparrFactory(original.ConnectionString);
        restarted.SettingsOverrides["IngestPath"] = original.Ingest;
        restarted.SettingsOverrides["LibraryPath"] = original.Library;
        restarted.SettingsOverrides["WorkerEnabled"] = "false";
        restarted.SettingsOverrides["MetadataProvidersEnabled"] = "false";
        restarted.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        restarted.SettingsOverrides["Authentication:Enabled"] = "false";
        restarted.SettingsOverrides["Authentication:KeysPath"] = Path.Combine(restarted.Root, "keys");
        using var restartedClient = restarted.CreateClient();

        await Worker(restarted).RunCycleAsync(default);

        var recovered = await restarted.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, recovered.Status);
        Assert.Equal(SourceCleanupStatus.Retained, recovered.CleanupStatus);
        Assert.True(File.Exists(source));
        Assert.Equal(recovered.FileHash, await new FileHashService().ComputeHashAsync(destination));
        using var retry = await restartedClient.PostAsync($"/api/imports/{id}/cleanup/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Concurrent_cleanup_claims_from_independent_contexts_delete_source_once()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var (id, source, destination) = await CompleteWithPendingCleanupAsync(app, client);

        using var firstScope = app.Services.CreateScope();
        using var secondScope = app.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var firstJob = await firstDb.ImportJobs.SingleAsync(job => job.Id == id);
        var secondJob = await secondDb.ImportJobs.SingleAsync(job => job.Id == id);
        var firstCleanup = firstScope.ServiceProvider.GetRequiredService<SourceCleanupService>();
        var secondCleanup = secondScope.ServiceProvider.GetRequiredService<SourceCleanupService>();

        await Task.WhenAll(
            firstCleanup.TryCleanupAsync(firstJob, default),
            secondCleanup.TryCleanupAsync(secondJob, default));

        var recovered = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, recovered.Status);
        Assert.Equal(SourceCleanupStatus.Completed, recovered.CleanupStatus);
        Assert.Equal(1, recovered.CleanupAttemptCount);
        Assert.False(File.Exists(source));
        Assert.Equal(recovered.FileHash, await new FileHashService().ComputeHashAsync(destination));
        using var verificationScope = app.Services.CreateScope();
        Assert.Equal(1, await verificationScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Books.CountAsync(book => book.ImportJobId == id));
    }

    private static async Task<(long Id, string Source, string Destination)> CompleteWithPendingCleanupAsync(
        PaparrFactory app, HttpClient client)
    {
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        app.Failure.CleanupClaimArmed = true;
        using var response = await client.PostAsync(
            $"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null);
        response.EnsureSuccessStatusCode();

        var completed = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, completed.Status);
        Assert.Equal(SourceCleanupStatus.Pending, completed.CleanupStatus);
        Assert.True(File.Exists(before.FilePath));
        var destination = Assert.IsType<string>(completed.DestinationPath);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ImportJobs.SingleAsync(item => item.Id == id);
        job.CleanupAfter = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        return (id, before.FilePath, destination);
    }

    private static BackgroundIngestionWorker Worker(PaparrFactory app) =>
        ActivatorUtilities.CreateInstance<BackgroundIngestionWorker>(app.Services);
}
