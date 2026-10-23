using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;
using Paparr.API.Services;
using Paparr.TestFixtures;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class FolderProfileIntegrationTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_rules_survive_configuration_removal_approval_and_undo(bool embedded)
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        Configure(app, 0, "Papers", "incoming/papers", "Research");
        app.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        var folder = Path.Combine(app.Ingest, "incoming", "papers");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "Ada Lovelace - Notes - Revised.pdf");
        PdfFixture.Write(source, embedded ? "Embedded book" : "", embedded ? "Embedded author" : "");
        var clock = new MutableClock();
        using var discovery = WithClock(app, clock);
        using var discoveryClient = discovery.CreateClient();
        await DiscoverAsync(discovery, clock);
        long id;
        using (var scope = discovery.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImportJobs.SingleAsync()).Id;

        using var restarted = new PaparrFactory(app.ConnectionString);
        restarted.SettingsOverrides["IngestPath"] = app.Ingest;
        restarted.SettingsOverrides["LibraryPath"] = app.Library;
        restarted.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        using var client = restarted.CreateClient();
        using (var scope = restarted.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = await db.ImportJobs.Include(j => j.Candidates).SingleAsync(j => j.Id == id);
            await scope.ServiceProvider.GetRequiredService<IEbookIngestionService>().ProcessImportJobAsync(pending);
        }
        var reviewed = (await client.GetFromJsonAsync<ImportJobDto>($"/api/imports/{id}"))!;
        Assert.Equal(new ImportProfileDto("Papers", "incoming/papers", "Research", "AuthorTitle"), reviewed.Profile);
        var choice = Assert.Single(reviewed.Candidates);
        Assert.Equal(embedded ? "Embedded book" : "Notes - Revised", choice.Title);
        Assert.Equal(embedded ? "Embedded author" : "Ada Lovelace", choice.Author);
        Assert.Equal(embedded ? "embedded" : "filename", choice.Source);
        if (!embedded) Assert.Contains("Papers", choice.Evidence);
        var preview = (await client.GetFromJsonAsync<ImportPreviewDto>($"/api/imports/{id}/preview/{choice.Id}"))!;
        Assert.StartsWith("Research/", preview.RelativePath);
        using var approvedResponse = await client.PostAsync($"/api/imports/{id}/accept/{choice.Id}", null);
        approvedResponse.EnsureSuccessStatusCode();
        var approved = (await approvedResponse.Content.ReadFromJsonAsync<ImportJobDto>())!;
        var imported = await app.ReadAsync(id);
        Assert.Equal(preview.RelativePath, Path.GetRelativePath(app.Library, imported.DestinationPath!).Replace('\\', '/'));
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(source));
        var undoPreview = (await client.GetFromJsonAsync<UndoPreviewDto>($"/api/imports/{id}/undo-preview"))!;
        using var undo = await client.PostAsJsonAsync($"/api/imports/{id}/undo", new { revision = undoPreview.Revision, operationId = Guid.NewGuid() });
        undo.EnsureSuccessStatusCode();
        var returned = (await undo.Content.ReadFromJsonAsync<ImportJobDto>())!;
        Assert.Equal(approved.Profile, returned.Profile);
        Assert.Equal("AwaitingApproval", returned.Status);
        using var reapproved = await client.PostAsync($"/api/imports/{id}/accept/{choice.Id}", null);
        reapproved.EnsureSuccessStatusCode();
        var final = await app.ReadAsync(id);
        Assert.Equal(imported.DestinationPath, final.DestinationPath);
        Assert.Equal(final.FileHash, await new FileHashService().ComputeHashAsync(final.DestinationPath!));
    }

    [Fact]
    public async Task Later_duplicates_keep_the_first_jobs_profile_and_revision()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        Configure(app, 0, "First", "first", "One");
        Configure(app, 1, "Second", "second", "Two");
        Directory.CreateDirectory(Path.Combine(app.Ingest, "first"));
        Directory.CreateDirectory(Path.Combine(app.Ingest, "second"));
        var first = Path.Combine(app.Ingest, "first", "Writer - Title.pdf");
        PdfFixture.Write(first);
        var clock = new MutableClock();
        using var host = WithClock(app, clock);
        using var client = host.CreateClient();
        await DiscoverAsync(host, clock);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await db.ImportJobs.AsNoTracking().SingleAsync();
        File.Copy(first, Path.Combine(app.Ingest, "second", "copy.pdf"));
        await DiscoverAsync(host, clock);
        var after = await db.ImportJobs.AsNoTracking().SingleAsync();
        Assert.Equal("First", after.ProfileName);
        Assert.Equal("One", after.CollectionSegment);
        Assert.Equal(original.Revision, after.Revision);
        Assert.Equal(1, await db.DuplicateSources.CountAsync());
    }

    [Fact]
    public async Task Existing_rows_gain_default_rules_without_losing_metadata()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var id = await app.SeedAsync();
        using (var scope = app.Services.CreateScope())
        {
            var migrator = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261022043936_RetainedSourceUndo");
            await migrator.MigrateAsync();
        }
        var preserved = await app.ReadAsync(id);
        Assert.Null(preserved.ProfileName);
        Assert.Null(preserved.CollectionSegment);
        Assert.Equal(FilenameOrder.TitleAuthor, preserved.FilenameOrder);
        Assert.Equal("The Time Machine", Assert.Single(preserved.Candidates).Title);
        Assert.True(File.Exists(preserved.FilePath));
    }

    [Fact]
    public async Task Unsafe_saved_collections_cannot_be_previewed_or_published()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var id = await app.SeedAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tracked = await db.ImportJobs.SingleAsync(j => j.Id == id);
            tracked.CollectionSegment = "../outside";
            await db.SaveChangesAsync();
        }
        var job = await app.ReadAsync(id);
        using var client = app.CreateClient();
        using var preview = await client.GetAsync($"/api/imports/{id}/preview/{job.Candidates.Single().Id}");
        using var approval = await client.PostAsync($"/api/imports/{id}/accept/{job.Candidates.Single().Id}", null);
        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, approval.StatusCode);
        Assert.DoesNotContain(app.Root, await approval.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(id)).Status);
        Assert.Empty(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.FilePath));
    }

    private static void Configure(PaparrFactory app, int index, string name, string folder, string collection)
    {
        app.SettingsOverrides[$"FolderProfiles:{index}:Name"] = name;
        app.SettingsOverrides[$"FolderProfiles:{index}:IngestSubfolder"] = folder;
        app.SettingsOverrides[$"FolderProfiles:{index}:Collection"] = collection;
        app.SettingsOverrides[$"FolderProfiles:{index}:FilenameOrder"] = "AuthorTitle";
    }

    private static WebApplicationFactory<Program> WithClock(PaparrFactory app, MutableClock clock) =>
        app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));

    private static async Task DiscoverAsync(WebApplicationFactory<Program> host, MutableClock clock)
    {
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ImportScanner>().ScanAsync(default);
        clock.Advance();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ImportScanner>().ScanAsync(default);
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now += TimeSpan.FromSeconds(6);
    }
}
