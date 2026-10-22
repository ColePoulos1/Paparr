using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;
using Paparr.IngestionBenchmarks;
using Testcontainers.PostgreSql;

var parsed = BenchmarkArguments.Parse(args);
if (parsed.Error is not null)
{
    Console.Error.WriteLine(parsed.Error);
    Console.Error.WriteLine("Usage: Paparr.IngestionBenchmarks [--files 2..100] [--content-mib 0..4] [--output <new-json-file>]");
    return 2;
}

var settings = parsed.Options!;
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var tempRoot = Path.Combine(Path.GetTempPath(), $"PaparrIngestionBenchmark-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);
try
{
    var stopwatch = Stopwatch.StartNew();
    await using var postgres = new PostgreSqlBuilder("postgres:18.6-alpine").Build();
    await postgres.StartAsync();
    stopwatch.Stop();
    var containerStartupMs = stopwatch.Elapsed.TotalMilliseconds;

    var clock = new BenchmarkClock(DateTimeOffset.UtcNow);
    var ingest = Path.Combine(tempRoot, "ingest");
    var library = Path.Combine(tempRoot, "library");
    Directory.CreateDirectory(ingest);
    Directory.CreateDirectory(library);
    var connectionString = postgres.GetConnectionString();
    using var factory = new BenchmarkFactory(connectionString, ingest, library, clock);
    stopwatch.Restart();
    using var client = factory.CreateClient();
    using (var health = await client.GetAsync("/health/live")) health.EnsureSuccessStatusCode();
    stopwatch.Stop();
    var hostStartupAndMigrationsMs = stopwatch.Elapsed.TotalMilliseconds;

    stopwatch.Restart();
    var inputs = BenchmarkFixtures.Create(ingest, settings);
    stopwatch.Stop();
    var fixtureCreationMs = stopwatch.Elapsed.TotalMilliseconds;
    var syntheticBytes = inputs.Sum(path => new FileInfo(path).Length);
    var pdfBytes = inputs.Where(path => Path.GetExtension(path) == ".pdf").Sum(path => new FileInfo(path).Length);
    var epubBytes = syntheticBytes - pdfBytes;

    var initialDiscoveryMs = await TimeAsync(async () =>
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ImportScanner>().ScanAsync(CancellationToken.None);
    });
    clock.Advance(TimeSpan.FromSeconds(5));
    var discoveryMs = await TimeAsync(async () =>
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ImportScanner>().ScanAsync(CancellationToken.None);
    });

    List<long> jobIds;
    using (var scope = factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        jobIds = await db.ImportJobs.AsNoTracking().OrderBy(job => job.Id).Select(job => job.Id).ToListAsync();
    }
    if (jobIds.Count != settings.Files) throw new InvalidOperationException("Not all synthetic files were discovered.");

    var metadataMs = 0d;
    foreach (var id in jobIds)
    {
        metadataMs += await TimeAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(item => item.Candidates).Include(item => item.Events)
                .AsSplitQuery().SingleAsync(item => item.Id == id);
            await scope.ServiceProvider.GetRequiredService<IEbookIngestionService>()
                .ProcessImportJobAsync(job, CancellationToken.None);
            if (job.Status != ImportStatus.AwaitingApproval || job.Candidates.Count == 0)
                throw new InvalidOperationException("Metadata extraction did not produce a review candidate.");
        });
    }

    var approvalPublicationMs = 0d;
    foreach (var id in jobIds)
    {
        approvalPublicationMs += await TimeAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(item => item.Candidates).Include(item => item.AcceptedBook)
                .Include(item => item.Events).AsSplitQuery().SingleAsync(item => item.Id == id);
            var candidate = job.Candidates.OrderByDescending(item => item.ConfidenceScore).ThenBy(item => item.Id).First();
            await scope.ServiceProvider.GetRequiredService<IEbookIngestionService>()
                .AcceptCandidateAsync(job, candidate, CancellationToken.None);
        });
    }

    var auditMs = await TimeAsync(async () =>
    {
        using var scope = factory.Services.CreateScope();
        var auditService = scope.ServiceProvider.GetRequiredService<LibraryAuditService>();
        var audit = await auditService.QueueAsync(CancellationToken.None);
        while (audit.IsActive)
        {
            await auditService.RunBatchAsync(CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Entry(audit).ReloadAsync();
        }
    });

    var stagingBefore = await ReadStageCountsAsync(factory);
    var stagingAfter = stagingBefore;
    var stagingMaintenanceMs = 0d;
    var stagingBatches = 0;
    while (stagingAfter.Pending > 0 && stagingBatches < settings.Files)
    {
        stagingMaintenanceMs += await TimeAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StagingService>().RunBatchAsync(CancellationToken.None);
        });
        stagingBatches++;
        stagingAfter = await ReadStageCountsAsync(factory);
    }
    var staging = new StagingSettlementResult(settings.Files, stagingBatches, stagingBefore, stagingAfter,
        Directory.GetFiles(library, "*.partial", SearchOption.AllDirectories).Length);
    stopwatch.Restart();
    var verification = await VerifyAsync(factory, inputs, jobIds, staging.AllChecksPassed);
    stopwatch.Stop();
    var finalVerificationMs = stopwatch.Elapsed.TotalMilliseconds;
    var result = new IngestionBenchmarkResult(
        Tool: "Paparr offline ingestion pipeline benchmark",
        Runtime: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        ProcessorCount: Environment.ProcessorCount,
        Configuration: new BenchmarkConfiguration(settings.Files, settings.ContentMiB, settings.RequestedContentBytes,
            syntheticBytes, pdfBytes, epubBytes, BenchmarkOptions.MaximumInputBytes, "PDF and EPUB fixtures",
            settings.ContentMiB == 0 ? "Tiny metadata fixtures."
                : "Uncompressed generated PDF page streams and referenced EPUB XHTML chapters; actual file bytes include format overhead.",
            "Metadata providers disabled; no external metadata requests."),
        Startup: new StartupMeasurements(containerStartupMs, hostStartupAndMigrationsMs, fixtureCreationMs),
        Pipeline: new PipelineMeasurements(initialDiscoveryMs, discoveryMs, 5, metadataMs,
            approvalPublicationMs, auditMs, stagingMaintenanceMs),
        Staging: staging,
        FinalVerificationMilliseconds: finalVerificationMs,
        Results: verification,
        Note: "Synthetic offline run using direct application services. Startup, migrations, fixture creation, simulated stability wait, final byte verification, and staging status probes are excluded from measured processing phases. Staging settlement records already-renamed, absent partial files; it does not measure orphan-byte reclamation. Retained originals and published copies occupy roughly twice the input bytes. This run does not measure rendering, human review, provider latency, network storage, or sustained production throughput.");

    var json = JsonSerializer.Serialize(result, jsonOptions);
    Console.WriteLine(json);
    if (settings.Output is not null)
    {
        var outputPath = Path.GetFullPath(settings.Output);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, result, jsonOptions);
        await output.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine));
    }

    return verification.AllChecksPassed ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Benchmark failed ({exception.GetType().Name}). Check Docker availability and retry.");
    return 1;
}
finally
{
    DeleteOwnedRoot(tempRoot);
}

static async Task<double> TimeAsync(Func<Task> action)
{
    var stopwatch = Stopwatch.StartNew();
    await action();
    stopwatch.Stop();
    return stopwatch.Elapsed.TotalMilliseconds;
}

static async Task<StageCounts> ReadStageCountsAsync(BenchmarkFactory factory)
{
    using var scope = factory.Services.CreateScope();
    var counts = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.AsNoTracking()
        .GroupBy(stage => stage.Status).Select(group => new { Status = group.Key, Count = group.Count() }).ToListAsync();
    int Count(PublicationStageStatus status) => counts.SingleOrDefault(item => item.Status == status)?.Count ?? 0;
    return new StageCounts(Count(PublicationStageStatus.Planned), Count(PublicationStageStatus.Owned),
        Count(PublicationStageStatus.Published), Count(PublicationStageStatus.Cleaned), Count(PublicationStageStatus.Retained));
}

static async Task<VerificationResult> VerifyAsync(BenchmarkFactory factory, IReadOnlyList<string> inputs,
    IReadOnlyList<long> jobIds, bool stagingSettled)
{
    var hashesMatch = true;
    var sourceHashesMatch = true;
    var sourcesRetained = true;
    using (var scope = factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = await db.ImportJobs.AsNoTracking().Include(item => item.AcceptedBook)
            .Where(item => jobIds.Contains(item.Id)).OrderBy(item => item.Id).ToListAsync();
        sourcesRetained = jobs.Count == inputs.Count && jobs.All(job => File.Exists(job.FilePath));
        foreach (var job in jobs)
        {
            if (!File.Exists(job.FilePath)) sourceHashesMatch = false;
            else
            {
                var actualSource = await scope.ServiceProvider.GetRequiredService<IFileHashService>()
                    .ComputeHashAsync(job.FilePath, CancellationToken.None);
                sourceHashesMatch &= string.Equals(actualSource, job.FileHash, StringComparison.OrdinalIgnoreCase);
            }
            if (job.Status != ImportStatus.Completed || string.IsNullOrWhiteSpace(job.DestinationPath) ||
                job.AcceptedBook?.FilePath != job.DestinationPath)
            {
                hashesMatch = false;
                continue;
            }
            var actual = await scope.ServiceProvider.GetRequiredService<IFileHashService>()
                .ComputeHashAsync(job.DestinationPath, CancellationToken.None);
            hashesMatch &= string.Equals(actual, job.FileHash, StringComparison.OrdinalIgnoreCase);
        }
        var audit = await db.LibraryAudits.AsNoTracking().OrderByDescending(item => item.Id).FirstOrDefaultAsync();
        return new VerificationResult(jobs.Count, jobs.Count(job => job.Status == ImportStatus.Completed),
            sourcesRetained, sourceHashesMatch, hashesMatch, stagingSettled,
            audit?.Status == AuditStatus.Completed && audit.ProcessedBooks == inputs.Count &&
            audit.VerifiedBooks == inputs.Count && audit.MissingBooks == 0 && audit.ChangedBooks == 0 && audit.UnavailableBooks == 0,
            audit?.ProcessedBooks ?? 0, audit?.VerifiedBooks ?? 0, audit?.MissingBooks ?? 0,
            audit?.ChangedBooks ?? 0, audit?.UnavailableBooks ?? 0,
            jobs.Sum(job => job.DestinationPath is not null && File.Exists(job.DestinationPath)
                ? new FileInfo(job.DestinationPath).Length : 0));
    }
}

static void DeleteOwnedRoot(string root)
{
    var fullRoot = Path.GetFullPath(root);
    var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    var prefix = temp + Path.DirectorySeparatorChar;
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (!fullRoot.StartsWith(prefix, comparison) || fullRoot.Equals(temp, comparison)) return;
    if (!Directory.Exists(fullRoot)) return;
    if (File.GetAttributes(fullRoot).HasFlag(FileAttributes.ReparsePoint)) return;
    Directory.Delete(fullRoot, recursive: true);
}

sealed class BenchmarkFactory(string connection, string ingest, string library, BenchmarkClock clock) : WebApplicationFactory<MetadataEnricherService>
{
    private readonly NpgsqlDataSource databaseSource = NpgsqlDataSource.Create(connection);
    private int sourceDisposed;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(Directory.GetCurrentDirectory(), "src", "Paparr.API"));
        builder.UseEnvironment("Benchmark");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection,
            ["IngestPath"] = ingest,
            ["LibraryPath"] = library,
            ["FileStableSeconds"] = "5",
            ["MaxFileSizeMb"] = "16",
            ["WorkerEnabled"] = "false",
            ["MetadataProvidersEnabled"] = "false",
            ["DeleteSourceAfterImport"] = "false",
            ["Authentication:Enabled"] = "false",
            ["Authentication:KeysPath"] = Path.Combine(Path.GetDirectoryName(ingest)!, "keys"),
            ["Database:ApplyMigrations"] = "true"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(databaseSource));
        });
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally
        {
            if (disposing && Interlocked.Exchange(ref sourceDisposed, 1) == 0) databaseSource.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally
        {
            if (Interlocked.Exchange(ref sourceDisposed, 1) == 0) await databaseSource.DisposeAsync();
        }
    }
}

sealed class BenchmarkClock(DateTimeOffset initialUtc) : TimeProvider
{
    private DateTimeOffset utcNow = initialUtc;
    private long timestamp;
    public override DateTimeOffset GetUtcNow() => utcNow;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public void Advance(TimeSpan duration)
    {
        utcNow = utcNow.Add(duration);
        timestamp += duration.Ticks;
    }
}

sealed record BenchmarkConfiguration(int Files, int ContentMiB, long RequestedContentBytes, long SyntheticBytes,
    long PdfBytes, long EpubBytes, long MaximumInputBytes, string FixtureTypes, string ContentMode, string ProviderMode);
sealed record StartupMeasurements(double PostgreSqlContainerMilliseconds, double HostAndMigrationsMilliseconds,
    double FixtureCreationMilliseconds);
sealed record PipelineMeasurements(double InitialDiscoveryMilliseconds, double DiscoveryMilliseconds,
    int SimulatedStabilityWaitSeconds, double MetadataExtractionAndReviewQueueMilliseconds,
    double ExplicitApprovalAndPublicationMilliseconds, double LibraryAuditMilliseconds, double StagingMaintenanceMilliseconds);
sealed record StageCounts(int Planned, int Owned, int Published, int Cleaned, int Retained)
{
    public int Total => Planned + Owned + Published + Cleaned + Retained;
    public int Pending => Planned + Owned + Published;
}
sealed record StagingSettlementResult(int ExpectedJournalRows, int BatchCalls, StageCounts Before, StageCounts After,
    int RemainingPartialFiles)
{
    public bool AllChecksPassed => Before.Total == ExpectedJournalRows && Before.Published == ExpectedJournalRows &&
        After.Total == ExpectedJournalRows && After.Cleaned == ExpectedJournalRows && After.Pending == 0 &&
        After.Retained == 0 && RemainingPartialFiles == 0;
}
sealed record VerificationResult(int DiscoveredJobs, int CompletedImports, bool AllSourcesRetained,
    bool AllSourceHashesVerified, bool AllPublishedHashesVerified, bool AllStagingJournalsSettled,
    bool AuditCompletedWithoutFaults, int AuditProcessed, int AuditVerified,
    int AuditMissing, int AuditChanged, int AuditUnavailable, long PublishedBytes)
{
    public bool AllChecksPassed => DiscoveredJobs > 0 && CompletedImports == DiscoveredJobs && AllSourcesRetained &&
        AllSourceHashesVerified && AllPublishedHashesVerified && AllStagingJournalsSettled && AuditCompletedWithoutFaults;
}
sealed record IngestionBenchmarkResult(string Tool, string Runtime, string OperatingSystem, string Architecture,
    int ProcessorCount, BenchmarkConfiguration Configuration, StartupMeasurements Startup,
    PipelineMeasurements Pipeline, StagingSettlementResult Staging, double FinalVerificationMilliseconds,
    VerificationResult Results, string Note);
