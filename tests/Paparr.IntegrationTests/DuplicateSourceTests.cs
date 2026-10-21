using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;
using Paparr.API.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Paparr.TestFixtures;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class DuplicateSourceTests(PostgresFixture postgres)
{
    private static readonly TimeSpan StabilityDelay = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Repeated_discovery_records_distinct_paths_without_changing_job_or_files()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var original = Path.Combine(app.Ingest, "original.pdf");
        PdfFixture.Write(original, "Duplicate title", "Duplicate author");
        var options = ScannerOptions(app);
        var clock = new MutableClock();
        var stability = new FileStabilityTracker(clock);
        Assert.False(stability.IsReady(new FileInfo(original), StabilityDelay));
        clock.Advance(StabilityDelay);

        var dbOptions = DatabaseOptions(app.ConnectionString);
        await using (var db = new AppDbContext(dbOptions))
            await Scanner(db, stability, clock, options).ScanAsync(default);

        await using var readDb = new AppDbContext(dbOptions);
        var job = await readDb.ImportJobs.AsNoTracking().SingleAsync();
        var originalBytes = await File.ReadAllBytesAsync(original);
        var duplicatePaths = new[] { "copy-a.pdf", "copy-b.pdf", "copy-c.pdf" }
            .Select(name => Path.Combine(app.Ingest, name)).ToArray();
        foreach (var path in duplicatePaths)
        {
            await File.WriteAllBytesAsync(path, originalBytes);
            Assert.False(stability.IsReady(new FileInfo(path), StabilityDelay));
        }
        clock.Advance(StabilityDelay);

        await using (var db = new AppDbContext(dbOptions))
            await Scanner(db, stability, clock, options).ScanAsync(default);

        var unchanged = await readDb.ImportJobs.AsNoTracking().SingleAsync();
        Assert.Equal(job.Id, unchanged.Id);
        Assert.Equal(job.Revision, unchanged.Revision);
        Assert.Equal(job.Status, unchanged.Status);
        Assert.Equal(job.FilePath, unchanged.FilePath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(original));
        Assert.All(duplicatePaths, path => Assert.Equal(originalBytes, File.ReadAllBytes(path)));
        Assert.Equal(3, await readDb.DuplicateSources.CountAsync());

        clock.Advance(StabilityDelay);
        await using (var db = new AppDbContext(dbOptions))
            await Scanner(db, stability, clock, options).ScanAsync(default);
        Assert.Equal(job.Revision, (await readDb.ImportJobs.AsNoTracking().SingleAsync()).Revision);
        Assert.Equal(3, await readDb.DuplicateSources.CountAsync());

        File.Delete(duplicatePaths[1]);
        var duplicateDate = await readDb.DuplicateSources.OrderBy(d => d.Id).Select(d => d.DiscoveredAt).FirstAsync();
        var outsidePath = Path.Combine(app.Ingest + "-sibling", "private-copy.pdf");
        readDb.DuplicateSources.Add(new DuplicateSource(job.Id, outsidePath, duplicateDate));
        await readDb.SaveChangesAsync();
        var firstPage = await client.GetFromJsonAsync<PagedResult<DuplicateSourceDto>>(
            $"/api/imports/{job.Id}/duplicates?page=1&pageSize=2");
        var secondPage = await client.GetFromJsonAsync<PagedResult<DuplicateSourceDto>>(
            $"/api/imports/{job.Id}/duplicates?page=2&pageSize=2");
        Assert.NotNull(firstPage);
        Assert.NotNull(secondPage);
        Assert.Equal(4, firstPage.Total);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(2, secondPage.Items.Count);
        var items = firstPage.Items.Concat(secondPage.Items).ToArray();
        Assert.Equal(duplicatePaths.Select(Path.GetFileName).Append("private-copy.pdf").Order().ToArray(),
            items.Select(item => item.FileName).Order().ToArray());
        Assert.All(items.Where(item => item.FileName != "private-copy.pdf"), item => Assert.NotNull(item.RelativePath));
        Assert.Null(items.Single(item => item.FileName == "private-copy.pdf").RelativePath);
        Assert.Contains("copy-b.pdf", items.Select(item => item.FileName));
        Assert.All(items, item => Assert.False(Path.IsPathRooted(item.RelativePath)));
        Assert.Equal(items.OrderBy(item => item.DiscoveredAt).ThenBy(item => item.Id).Select(item => item.Id),
            items.Select(item => item.Id));

        var response = await client.GetAsync($"/api/imports/{job.Id}/duplicates?page=1&pageSize=100");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(app.Root, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/imports/{job.Id}/duplicates?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/imports/{job.Id}/duplicates?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/imports/{job.Id}/duplicates?page=100001")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/imports/999999/duplicates")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_discovery_records_the_losing_path_after_hash_conflict()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var firstRoot = Path.Combine(app.Ingest, "left");
        var secondRoot = Path.Combine(app.Ingest, "right");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var first = Path.Combine(firstRoot, "first.pdf");
        PdfFixture.Write(first, "Concurrent title", "Concurrent author");
        var second = Path.Combine(secondRoot, "second.pdf");
        File.Copy(first, second);

        var clock = new MutableClock();
        var trackers = new[] { new FileStabilityTracker(clock), new FileStabilityTracker(clock) };
        foreach (var tracker in trackers)
        {
            Assert.False(tracker.IsReady(new FileInfo(first), StabilityDelay));
            Assert.False(tracker.IsReady(new FileInfo(second), StabilityDelay));
        }
        clock.Advance(StabilityDelay);

        var barrier = new ConcurrentJobInsertBarrier();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(app.ConnectionString)
            .AddInterceptors(barrier).Options;
        var firstOptions = ScannerOptions(app, firstRoot);
        var secondOptions = ScannerOptions(app, secondRoot);
        await using var firstDb = new AppDbContext(dbOptions);
        await using var secondDb = new AppDbContext(dbOptions);
        var firstScanner = Scanner(firstDb, trackers[0], clock, firstOptions);
        var secondScanner = Scanner(secondDb, trackers[1], clock, secondOptions);

        await Task.WhenAll(firstScanner.ScanAsync(default), secondScanner.ScanAsync(default));

        await using var verify = new AppDbContext(DatabaseOptions(app.ConnectionString));
        var job = await verify.ImportJobs.SingleAsync();
        var duplicate = await verify.DuplicateSources.SingleAsync();
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(duplicate.FilePath));
        Assert.False(SourcePathIdentity.SamePath(job.FilePath, duplicate.FilePath));
        Assert.Equal(ImportStatus.Pending, job.Status);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    private static IngestionOptions ScannerOptions(PaparrFactory app, string? ingestPath = null) => new()
    {
        IngestPath = ingestPath ?? app.Ingest,
        LibraryPath = app.Library,
        FileStableSeconds = (int)StabilityDelay.TotalSeconds
    };

    private static DbContextOptions<AppDbContext> DatabaseOptions(string connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;

    private static ImportScanner Scanner(AppDbContext db, FileStabilityTracker stability,
        TimeProvider clock, IngestionOptions options) => new(db, new FileHashService(), stability,
        new StableFileHashCache(clock), Options.Create(options), clock, NullLogger<ImportScanner>.Instance);

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now = now.Add(amount);
    }

    private sealed class ConcurrentJobInsertBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ImportJob>().Any(entry => entry.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref arrivals) == 2) bothArrived.TrySetResult();
                await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
}
