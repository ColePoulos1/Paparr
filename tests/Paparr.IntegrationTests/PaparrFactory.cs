using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;
using Paparr.TestFixtures;

namespace Paparr.IntegrationTests;

public sealed class PaparrFactory(string connection) : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "paparr-integration", Guid.NewGuid().ToString("N"));
    public string Ingest => Path.Combine(Root, "ingest");
    public string Library => Path.Combine(Root, "library");
    public CommitFailure Failure { get; } = new();
    public Dictionary<string, string?> SettingsOverrides { get; } = new();
    public string ConnectionString => connection;

    public static async Task<PaparrFactory> CreateAsync(PostgresFixture postgres)
    {
        var database = "paparr_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(postgres.Container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await command.ExecuteNonQueryAsync();
        var settings = new NpgsqlConnectionStringBuilder(postgres.Container.GetConnectionString()) { Database = database };
        var factory = new PaparrFactory(settings.ConnectionString);
        Directory.CreateDirectory(factory.Ingest);
        Directory.CreateDirectory(factory.Library);
        return factory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connection,
                ["IngestPath"] = Ingest,
                ["LibraryPath"] = Library,
                ["WorkerEnabled"] = "false",
                ["MetadataProvidersEnabled"] = "false",
                ["DeleteSourceAfterImport"] = "true",
                ["Authentication:Enabled"] = "false",
                ["Authentication:KeysPath"] = Path.Combine(Root, "keys")
            };
            foreach (var (key, value) in SettingsOverrides) settings[key] = value;
            config.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(Failure)));
    }

    public async Task<long> SeedAsync(ImportStatus status = ImportStatus.AwaitingApproval)
    {
        var path = Path.Combine(Ingest, Guid.NewGuid().ToString("N") + ".pdf");
        PdfFixture.Write(path, "The Time Machine", "H. G. Wells");
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var job = new ImportJob
        {
            FilePath = path,
            FileHash = await new FileHashService().ComputeHashAsync(path),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            Candidates = [new MetadataCandidate
            {
                Title = "The Time Machine", Author = "H. G. Wells", Source = "embedded", ExternalId = "",
                ConfidenceScore = 85, CreatedAt = now
            }]
        };
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    public async Task<ImportJob> ReadAsync(long id)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImportJobs.AsNoTracking()
            .Include(j => j.Candidates).Include(j => j.AcceptedBook).Include(j => j.Events).AsSplitQuery().SingleAsync(j => j.Id == id);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

public sealed class CommitFailure : SaveChangesInterceptor
{
    public bool Armed { get; set; }
    public bool CleanupClaimArmed { get; set; }
    public bool CleanupCompletionArmed { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var jobs = eventData.Context!.ChangeTracker.Entries<ImportJob>().Where(e => e.State == EntityState.Modified);
        if (CleanupClaimArmed && jobs.Any(e => e.Entity.CleanupStatus == SourceCleanupStatus.Pending && e.Entity.CleanupAttemptCount > 0))
        {
            CleanupClaimArmed = false;
            throw new DbUpdateException("Injected cleanup claim failure.");
        }
        if (CleanupCompletionArmed && jobs.Any(e => e.Entity.CleanupStatus == SourceCleanupStatus.Completed))
        {
            CleanupCompletionArmed = false;
            throw new DbUpdateException("Injected cleanup completion failure.");
        }
        if (Armed && eventData.Context!.ChangeTracker.Entries<ImportJob>().Any(e =>
            e.State == EntityState.Modified && e.Entity.Status == ImportStatus.Completed))
        {
            Armed = false;
            throw new DbUpdateException("Injected commit failure.");
        }
        return ValueTask.FromResult(result);
    }
}
