using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class SourceCleanupTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Completion_commit_persists_cleanup_intent_before_any_source_removal()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        app.Failure.CleanupClaimArmed = true;
        (await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null)).EnsureSuccessStatusCode();
        var interrupted = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, interrupted.Status);
        Assert.Equal(SourceCleanupStatus.Pending, interrupted.CleanupStatus);
        Assert.True(File.Exists(before.FilePath));
        Assert.NotNull(interrupted.AcceptedBook);

        await Worker(app).RunCycleAsync(default);
        var recovered = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, recovered.Status);
        Assert.Equal(SourceCleanupStatus.Completed, recovered.CleanupStatus);
        Assert.False(File.Exists(before.FilePath));
        Assert.Equal(before.FileHash, await new FileHashService().ComputeHashAsync(recovered.DestinationPath!));
    }

    [Fact]
    public async Task Interruption_after_source_removal_replays_cleanup_without_reimporting()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        app.Failure.CleanupCompletionArmed = true;
        (await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null)).EnsureSuccessStatusCode();
        var interrupted = await app.ReadAsync(id);
        Assert.Equal(SourceCleanupStatus.Pending, interrupted.CleanupStatus);
        Assert.False(File.Exists(before.FilePath));
        await ExpireCleanup(app, id);

        await Worker(app).RunCycleAsync(default);
        var recovered = await app.ReadAsync(id);
        Assert.Equal(SourceCleanupStatus.Completed, recovered.CleanupStatus);
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Books.CountAsync());
    }

    [Fact]
    public async Task Changed_source_is_retained_and_cleanup_can_be_retried_after_restoration()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        var original = await File.ReadAllBytesAsync(before.FilePath);
        app.Failure.CleanupClaimArmed = true;
        (await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null)).EnsureSuccessStatusCode();
        await File.WriteAllTextAsync(before.FilePath, "a different file at the old path");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ExpireCleanup(app, id);
            await Worker(app).RunCycleAsync(default);
        }
        var retained = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, retained.Status);
        Assert.Equal(SourceCleanupStatus.Retained, retained.CleanupStatus);
        Assert.Equal(3, retained.CleanupAttemptCount);
        Assert.Equal("a different file at the old path", await File.ReadAllTextAsync(before.FilePath));
        Assert.NotNull(retained.CleanupError);
        Assert.Null(retained.LastError);

        await File.WriteAllBytesAsync(before.FilePath, original);
        (await client.PostAsync($"/api/imports/{id}/cleanup/retry", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/imports/{id}/cleanup/retry", null)).EnsureSuccessStatusCode();
        await Worker(app).RunCycleAsync(default);
        Assert.Equal(SourceCleanupStatus.Completed, (await app.ReadAsync(id)).CleanupStatus);
        Assert.False(File.Exists(before.FilePath));
    }

    [Fact]
    public async Task Cleanup_cannot_be_requested_for_an_unapproved_import()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/imports/{id}/cleanup/retry", null)).StatusCode);
        Assert.True(File.Exists((await app.ReadAsync(id)).FilePath));
    }

    [Fact]
    public async Task A_replacement_directory_leaves_cleanup_pending_and_the_import_complete()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        app.Failure.CleanupClaimArmed = true;
        (await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null)).EnsureSuccessStatusCode();
        File.Delete(before.FilePath);
        Directory.CreateDirectory(before.FilePath);
        var replacement = Path.Combine(before.FilePath, "keep.txt");
        await File.WriteAllTextAsync(replacement, "unrelated content");

        await Worker(app).RunCycleAsync(default);

        var after = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, after.Status);
        Assert.Equal(SourceCleanupStatus.Pending, after.CleanupStatus);
        Assert.Equal(1, after.CleanupAttemptCount);
        Assert.NotNull(after.CleanupError);
        Assert.Equal("unrelated content", await File.ReadAllTextAsync(replacement));
        Assert.Equal(before.FileHash, await new FileHashService().ComputeHashAsync(after.DestinationPath!));
    }

    private static BackgroundIngestionWorker Worker(PaparrFactory app) =>
        ActivatorUtilities.CreateInstance<BackgroundIngestionWorker>(app.Services);

    private static async Task ExpireCleanup(PaparrFactory app, long id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ImportJobs.SingleAsync(j => j.Id == id);
        job.CleanupAfter = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }
}
