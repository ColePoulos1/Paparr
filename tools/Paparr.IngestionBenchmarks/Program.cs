using System.Diagnostics;
using System.IO.Compression;
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
using Paparr.TestFixtures;
using Testcontainers.PostgreSql;

var parsed = BenchmarkArguments.Parse(args);
if (parsed.Error is not null)
{
    Console.Error.WriteLine(parsed.Error);
    Console.Error.WriteLine("Usage: Paparr.IngestionBenchmarks [--files 2..100] [--output <new-json-file>]");
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
    var inputs = CreateFixtures(ingest, settings.Files);
    stopwatch.Stop();
    var fixtureCreationMs = stopwatch.Elapsed.TotalMilliseconds;
    var syntheticBytes = inputs.Sum(path => new FileInfo(path).Length);

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
                .SingleAsync(item => item.Id == id);
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
                .Include(item => item.Events).SingleAsync(item => item.Id == id);
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

    var verification = await VerifyAsync(factory, inputs, jobIds);
    var result = new IngestionBenchmarkResult(
        Tool: "Paparr offline ingestion pipeline benchmark",
        Runtime: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        ProcessorCount: Environment.ProcessorCount,
        Configuration: new BenchmarkConfiguration(settings.Files, syntheticBytes, "PDF and EPUB fixtures",
            "Metadata providers disabled; no external metadata requests."),
        Startup: new StartupMeasurements(containerStartupMs, hostStartupAndMigrationsMs, fixtureCreationMs),
        Pipeline: new PipelineMeasurements(initialDiscoveryMs, discoveryMs, 5, metadataMs,
            approvalPublicationMs, auditMs),
        Results: verification,
        Note: "Synthetic offline run. Database/container startup, migrations, fixture creation, and the simulated stability wait are reported separately from processing phases. Results are for this run only and do not claim production throughput.");

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

static string[] CreateFixtures(string ingest, int count)
{
    var paths = new string[count];
    for (var index = 0; index < count; index++)
    {
        var title = $"Benchmark Book {index + 1:D3}";
        var author = $"Benchmark Author {index + 1:D3}";
        var extension = index % 2 == 0 ? ".pdf" : ".epub";
        var path = Path.Combine(ingest, $"synthetic-{index + 1:D3}{extension}");
        if (extension == ".pdf") PdfFixture.Write(path, title, author);
        else WriteEpub(path, title, author);
        paths[index] = path;
    }
    return paths;
}

static void WriteEpub(string path, string title, string author)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    Add(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
    Add(archive, "META-INF/container.xml",
        """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
    Add(archive, "content.opf",
        $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:uuid:{Guid.NewGuid():D}</dc:identifier><dc:title>{System.Security.SecurityElement.Escape(title)}</dc:title><dc:creator>{System.Security.SecurityElement.Escape(author)}</dc:creator><dc:language>en</dc:language></metadata><manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest><spine><itemref idref="chapter"/></spine></package>""");
    Add(archive, "chapter.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Offline benchmark fixture.</p></body></html>");
    Add(archive, "nav.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body><nav epub:type=\"toc\"><ol><li><a href=\"chapter.xhtml\">Chapter</a></li></ol></nav></body></html>");
}

static void Add(ZipArchive archive, string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
{
    using var writer = new StreamWriter(archive.CreateEntry(path, compression).Open());
    writer.Write(content);
}

static async Task<VerificationResult> VerifyAsync(BenchmarkFactory factory, IReadOnlyList<string> inputs, IReadOnlyList<long> jobIds)
{
    var hashesMatch = true;
    var sourcesRetained = true;
    using (var scope = factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = await db.ImportJobs.AsNoTracking().Include(item => item.AcceptedBook)
            .Where(item => jobIds.Contains(item.Id)).OrderBy(item => item.Id).ToListAsync();
        sourcesRetained = jobs.Count == inputs.Count && jobs.All(job => File.Exists(job.FilePath));
        foreach (var job in jobs)
        {
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
            sourcesRetained, hashesMatch, audit?.Status == AuditStatus.Completed && audit.ProcessedBooks == inputs.Count &&
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
        });
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

sealed record BenchmarkOptions(int Files, string? Output);
sealed record BenchmarkArguments(BenchmarkOptions? Options, string? Error)
{
    public static BenchmarkArguments Parse(string[] args)
    {
        var files = 10;
        string? output = null;
        for (var index = 0; index < args.Length; index++)
        {
            var flag = args[index];
            if (++index >= args.Length) return new(null, $"Missing value for {flag}.");
            var value = args[index];
            if (flag == "--files" && int.TryParse(value, out files) && files is >= 2 and <= 100) continue;
            if (flag == "--output" && !string.IsNullOrWhiteSpace(value)) { output = value; continue; }
            return new(null, $"Unknown option or invalid value: {flag} {value}.");
        }
        return new(new BenchmarkOptions(files, output), null);
    }
}

sealed record BenchmarkConfiguration(int Files, long SyntheticBytes, string FixtureTypes, string ProviderMode);
sealed record StartupMeasurements(double PostgreSqlContainerMilliseconds, double HostAndMigrationsMilliseconds,
    double FixtureCreationMilliseconds);
sealed record PipelineMeasurements(double InitialDiscoveryMilliseconds, double DiscoveryMilliseconds,
    int SimulatedStabilityWaitSeconds, double MetadataExtractionAndReviewQueueMilliseconds,
    double ExplicitApprovalAndPublicationMilliseconds, double LibraryAuditMilliseconds);
sealed record VerificationResult(int DiscoveredJobs, int CompletedImports, bool AllSourcesRetained,
    bool AllPublishedHashesVerified, bool AuditCompletedWithoutFaults, int AuditProcessed, int AuditVerified,
    int AuditMissing, int AuditChanged, int AuditUnavailable, long PublishedBytes)
{
    public bool AllChecksPassed => DiscoveredJobs > 0 && CompletedImports == DiscoveredJobs && AllSourcesRetained &&
        AllPublishedHashesVerified && AuditCompletedWithoutFaults;
}
sealed record IngestionBenchmarkResult(string Tool, string Runtime, string OperatingSystem, string Architecture,
    int ProcessorCount, BenchmarkConfiguration Configuration, StartupMeasurements Startup,
    PipelineMeasurements Pipeline, VerificationResult Results, string Note);
