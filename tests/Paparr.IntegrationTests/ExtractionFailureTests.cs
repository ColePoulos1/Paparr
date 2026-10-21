using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class ExtractionFailureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_source_changed_after_discovery_fails_without_publishing_or_deleting_it()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync(ImportStatus.Pending);
        var before = await app.ReadAsync(id);
        await File.WriteAllTextAsync(before.FilePath, "a different ebook at the same path");

        await Worker(app).RunCycleAsync(default);

        var after = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Failed, after.Status);
        Assert.Equal(1, after.AttemptCount);
        Assert.Contains("source changed after discovery", after.LastError);
        Assert.Null(after.AcceptedBook);
        Assert.Equal("a different ebook at the same path", await File.ReadAllTextAsync(before.FilePath));
        Assert.Empty(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_corrupt_discovered_file_records_failure_and_bounded_retry_preserves_its_bytes()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["MaxAttempts"] = "1";
        using var client = app.CreateClient();
        var id = await app.SeedAsync(ImportStatus.Pending);
        var before = await app.ReadAsync(id);
        await File.WriteAllTextAsync(before.FilePath, "incomplete PDF fixture");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.SingleAsync(item => item.Id == id);
            job.FileHash = await new FileHashService().ComputeHashAsync(job.FilePath);
            await db.SaveChangesAsync();
        }

        await Worker(app).RunCycleAsync(default);

        var after = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Failed, after.Status);
        Assert.Equal(1, after.AttemptCount);
        Assert.Contains("could not be read", after.LastError);
        Assert.Null(after.LeaseExpiresAt);
        Assert.Null(after.AcceptedBook);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/imports/{id}/retry", null)).StatusCode);
        Assert.Equal("incomplete PDF fixture", await File.ReadAllTextAsync(before.FilePath));
        Assert.Empty(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Oversize_files_and_exhausted_attempts_fail_before_reading_metadata()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["MaxFileSizeMb"] = "1";
        using var client = app.CreateClient();
        var id = await app.SeedAsync(ImportStatus.Pending);
        var before = await app.ReadAsync(id);
        await using (var source = new FileStream(before.FilePath, FileMode.Open, FileAccess.Write))
            source.SetLength(1024 * 1024 + 1);
        var hash = await new FileHashService().ComputeHashAsync(before.FilePath);

        await Worker(app).RunCycleAsync(default);

        var after = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Failed, after.Status);
        Assert.Contains("size limit", after.LastError);
        Assert.Equal(hash, await new FileHashService().ComputeHashAsync(before.FilePath));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.SingleAsync(item => item.Id == id);
            job.Status = ImportStatus.Pending;
            job.AttemptCount = 3;
            await db.SaveChangesAsync();
        }

        await Worker(app).RunCycleAsync(default);

        after = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Failed, after.Status);
        Assert.Equal(3, after.AttemptCount);
        Assert.Contains("attempt limit", after.LastError);
        Assert.Equal(hash, await new FileHashService().ComputeHashAsync(before.FilePath));
        Assert.Empty(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
    }

    private static BackgroundIngestionWorker Worker(PaparrFactory app) =>
        ActivatorUtilities.CreateInstance<BackgroundIngestionWorker>(app.Services);
}
