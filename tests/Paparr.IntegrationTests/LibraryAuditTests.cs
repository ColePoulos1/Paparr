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
public sealed class LibraryAuditTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Audit_classifies_library_files_and_exposes_filtered_relative_results()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var paths = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var id = await app.SeedAsync();
            var before = await app.ReadAsync(id);
            (await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null))
                .EnsureSuccessStatusCode();
            paths.Add((await app.ReadAsync(id)).AcceptedBook!.FilePath!);
        }

        var verifiedBytes = await File.ReadAllBytesAsync(paths[0]);
        var changedBytes = System.Text.Encoding.UTF8.GetBytes("user-modified library content");
        await File.WriteAllBytesAsync(paths[1], changedBytes);
        File.Delete(paths[2]);

        var queuedResponse = await client.PostAsync("/api/library/audits", null);
        Assert.Equal(HttpStatusCode.Accepted, queuedResponse.StatusCode);
        var queued = await queuedResponse.Content.ReadFromJsonAsync<LibraryAuditDto>();
        Assert.NotNull(queued);
        Assert.Equal(3, queued.TotalBooks);

        await RunBatchAsync(app);

        var completed = await client.GetFromJsonAsync<LibraryAuditDto>(
            $"/api/library/audits/{queued.Id}");
        Assert.NotNull(completed);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(3, completed.ProcessedBooks);
        Assert.Equal(1, completed.VerifiedBooks);
        Assert.Equal(1, completed.ChangedBooks);
        Assert.Equal(1, completed.MissingBooks);

        Assert.Equal(verifiedBytes, await File.ReadAllBytesAsync(paths[0]));
        Assert.Equal(changedBytes, await File.ReadAllBytesAsync(paths[1]));
        Assert.False(File.Exists(paths[2]));

        var verified = await client.GetFromJsonAsync<LibraryPageDto>(
            "/api/library?status=Verified&page=1&pageSize=10");
        Assert.NotNull(verified);
        Assert.Equal(1, verified.Total);
        Assert.Equal(1, verified.Counts["Verified"]);
        var verifiedBook = Assert.Single(verified.Items);
        Assert.Equal("Verified", verifiedBook.IntegrityStatus);
        Assert.NotNull(verifiedBook.RelativePath);
        Assert.False(Path.IsPathRooted(verifiedBook.RelativePath));
        Assert.DoesNotContain(app.Root, verifiedBook.RelativePath, StringComparison.OrdinalIgnoreCase);

        var page = await client.GetFromJsonAsync<LibraryPageDto>(
            "/api/library?page=2&pageSize=2");
        Assert.NotNull(page);
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Page);
        Assert.Single(page.Items);
        Assert.Equal(1, page.Counts["Verified"]);
        Assert.Equal(1, page.Counts["Changed"]);
        Assert.Equal(1, page.Counts["Missing"]);
    }

    [Fact]
    public async Task Concurrent_start_requests_share_one_audit_and_expired_batch_resumes_at_saved_cursor()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        await SeedCompletedBooksAsync(app, 21);

        var responses = await Task.WhenAll(
            client.PostAsync("/api/library/audits", null),
            client.PostAsync("/api/library/audits", null),
            client.PostAsync("/api/library/audits", null));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var audits = await Task.WhenAll(responses.Select(response =>
            response.Content.ReadFromJsonAsync<LibraryAuditDto>()));
        Assert.All(audits, audit => Assert.NotNull(audit));
        var activeId = audits[0]!.Id;
        Assert.All(audits, audit => Assert.Equal(activeId, audit!.Id));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.LibraryAudits.CountAsync(audit => audit.IsActive));
        }

        await RunBatchAsync(app);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.LibraryAudits.SingleAsync(item => item.Id == activeId);
            Assert.Equal(AuditStatus.Running, audit.Status);
            Assert.True(audit.IsActive);
            Assert.Equal(20, audit.ProcessedBooks);
            Assert.Equal(20, audit.LastBookId);
            audit.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await RunBatchAsync(app);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.LibraryAudits.SingleAsync(item => item.Id == activeId);
            Assert.Equal(AuditStatus.Completed, audit.Status);
            Assert.False(audit.IsActive);
            Assert.Equal(21, audit.ProcessedBooks);
            Assert.Equal(21, audit.VerifiedBooks);
            Assert.Equal(audit.MaximumBookId, audit.LastBookId);
        }

        var nextResponse = await client.PostAsync("/api/library/audits", null);
        Assert.Equal(HttpStatusCode.Accepted, nextResponse.StatusCode);
        var next = await nextResponse.Content.ReadFromJsonAsync<LibraryAuditDto>();
        Assert.NotNull(next);
        Assert.NotEqual(activeId, next.Id);
        using var finalScope = app.Services.CreateScope();
        Assert.Equal(1, await finalScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .LibraryAudits.CountAsync(audit => audit.IsActive));
    }

    private static async Task RunBatchAsync(PaparrFactory app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LibraryAuditService>().RunBatchAsync(default);
    }

    private static async Task SeedCompletedBooksAsync(PaparrFactory app, int count)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(app.Library, "audit-seed", $"book-{index:D2}.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, $"disposable audit fixture {index}");
            var now = DateTime.UtcNow;
            var hash = await new FileHashService().ComputeHashAsync(path);
            var job = new ImportJob
            {
                FilePath = path,
                DestinationPath = path,
                FileHash = hash,
                Status = ImportStatus.Completed,
                CleanupStatus = SourceCleanupStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.ImportJobs.Add(job);
            db.Books.Add(new Book
            {
                Title = $"Audit book {index}",
                Author = "Test Author",
                Source = "integration-test",
                ExternalId = "",
                FilePath = path,
                ImportedAt = now,
                ImportJob = job
            });
        }
        await db.SaveChangesAsync();
    }
}
