using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class ImportTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Upgrading_the_original_schema_preserves_existing_jobs()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(app.ConnectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
            await migrator.MigrateAsync("20260119225604_InitialCreate");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "ImportJobs" ("FilePath", "FileHash", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('legacy.epub', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', 'Pending', NOW(), NOW())
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Books" ("Title", "Author", "Source", "ExternalId", "ImportedAt", "ImportJobId")
                SELECT 'Legacy title', 'Legacy author', 'embedded', '', NOW(), "Id" FROM "ImportJobs"
                """);
        }
        using var client = app.CreateClient();
        var response = await client.GetFromJsonAsync<PagedResult<ImportJobDto>>("/api/imports");
        Assert.NotNull(response);
        var job = Assert.Single(response.Items);
        Assert.Equal("legacy.epub", job.FileName);
        Assert.Equal("Pending", job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal("NotRequested", job.Cleanup.Status);
        var library = await client.GetFromJsonAsync<LibraryPageDto>("/api/library");
        Assert.Equal("Unchecked", Assert.Single(library!.Items).IntegrityStatus);
    }

    [Fact]
    public async Task Approval_commits_one_book_and_repeated_approval_is_idempotent()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        var endpoint = $"/api/imports/{id}/accept/{before.Candidates.Single().Id}";
        (await client.PostAsync(endpoint, null)).EnsureSuccessStatusCode();
        (await client.PostAsync(endpoint, null)).EnsureSuccessStatusCode();
        var after = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, after.Status);
        Assert.NotNull(after.AcceptedBook);
        Assert.True(File.Exists(after.AcceptedBook.FilePath));
        Assert.False(File.Exists(before.FilePath));
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Books.CountAsync());
    }

    [Fact]
    public async Task Retry_is_consumed_by_the_worker_without_rediscovering_the_file()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync(ImportStatus.Failed);
        (await client.PostAsync($"/api/imports/{id}/retry", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/imports/{id}/retry", null)).EnsureSuccessStatusCode();
        var worker = ActivatorUtilities.CreateInstance<BackgroundIngestionWorker>(app.Services);
        await worker.RunCycleAsync(default);
        var after = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.AwaitingApproval, after.Status);
        Assert.Equal(1, after.AttemptCount);
        Assert.Single(after.Candidates);
        Assert.Contains(after.Events, e => e.Message == "Retry queued.");
    }

    [Fact]
    public async Task Failed_database_commit_retains_source_and_recovery_finishes_existing_publication()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        app.Failure.Armed = true;
        var response = await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var interrupted = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Publishing, interrupted.Status);
        Assert.True(File.Exists(before.FilePath));
        Assert.True(File.Exists(interrupted.DestinationPath));
        Assert.Null(interrupted.AcceptedBook);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(j => j.Candidates).SingleAsync(j => j.Id == id);
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await ActivatorUtilities.CreateInstance<BackgroundIngestionWorker>(app.Services).RunCycleAsync(default);
        var recovered = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, recovered.Status);
        Assert.False(File.Exists(before.FilePath));
        Assert.Single(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
        Assert.Contains(recovered.Events, e => e.Message == "Recovered an interrupted attempt.");
    }

    [Fact]
    public async Task Concurrent_approvals_cannot_create_two_books()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        var endpoint = $"/api/imports/{id}/accept/{before.Candidates.Single().Id}";
        var responses = await Task.WhenAll(client.PostAsync(endpoint, null), client.PostAsync(endpoint, null));
        Assert.Contains(responses, r => r.IsSuccessStatusCode);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode || r.StatusCode == HttpStatusCode.Conflict));
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Books.CountAsync());
    }

    [Fact]
    public async Task A_destination_collision_preserves_both_original_files()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        var candidate = before.Candidates.Single();
        var destination = LibraryPaths.Destination(app.Library, candidate.Title, candidate.Author, ".pdf", before.FileHash);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "another book");
        var response = await client.PostAsync($"/api/imports/{id}/accept/{candidate.Id}", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("another book", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(before.FilePath));
        Assert.Equal(ImportStatus.Failed, (await app.ReadAsync(id)).Status);
    }

    [Fact]
    public async Task Preview_does_not_write_files_or_change_state()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        var preview = await client.GetFromJsonAsync<ImportPreviewDto>($"/api/imports/{id}/preview/{before.Candidates.Single().Id}");
        Assert.NotNull(preview);
        Assert.False(Path.IsPathRooted(preview.RelativePath));
        Assert.Empty(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
        Assert.Equal(before.Revision, (await app.ReadAsync(id)).Revision);
    }

    [Fact]
    public async Task Manual_metadata_and_pagination_are_exposed_without_server_paths()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        (await client.PostAsJsonAsync($"/api/imports/{id}/metadata", new { title = "Corrected title", author = "Correct author" })).EnsureSuccessStatusCode();
        var result = await client.GetFromJsonAsync<PagedResult<ImportJobDto>>("/api/imports?search=Corrected&pageSize=1");
        Assert.NotNull(result);
        Assert.Equal(1, result.Total);
        var job = Assert.Single(result.Items);
        Assert.DoesNotContain(app.Root, job.FileName);
        Assert.Contains(job.Candidates, c => c.Source == "manual" && c.Title == "Corrected title");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/imports?pageSize=1000")).StatusCode);
    }

    [Fact]
    public async Task Foreign_candidates_and_invalid_transitions_are_rejected()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var first = await app.SeedAsync();
        var second = await app.SeedAsync(ImportStatus.Pending);
        var candidate = (await app.ReadAsync(first)).Candidates.Single();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/imports/{second}/accept/{candidate.Id}", null)).StatusCode);
        var pendingCandidate = (await app.ReadAsync(second)).Candidates.Single();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/imports/{second}/accept/{pendingCandidate.Id}", null)).StatusCode);
    }

    [Fact]
    public async Task PostgreSql_enforces_unique_content_and_the_migrations_match_the_model()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var existing = await app.ReadAsync(id);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.True((await db.Database.GetAppliedMigrationsAsync()).Count() >= 2);
        db.ImportJobs.Add(new ImportJob
        {
            FilePath = existing.FilePath,
            FileHash = existing.FileHash,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
