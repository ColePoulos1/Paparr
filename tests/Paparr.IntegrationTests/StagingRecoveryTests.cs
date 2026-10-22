using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class StagingRecoveryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Upgrading_an_interrupted_publication_assigns_a_fence_and_retains_legacy_temporary_files()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        var id = await app.SeedAsync(ImportStatus.Publishing);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(j => j.Candidates).SingleAsync(j => j.Id == id);
            var candidate = job.Candidates.Single();
            job.SelectedCandidateId = candidate.Id;
            job.DestinationPath = LibraryPaths.Destination(app.Library, candidate.Title, candidate.Author, ".pdf", job.FileHash);
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261021224910_EditionMetadata");
            await migrator.MigrateAsync();
        }
        var legacy = Path.Combine(app.Library, "legacy.epub.unknown.partial");
        await File.WriteAllTextAsync(legacy, "unconfirmed legacy bytes");
        Assert.Null((await app.ReadAsync(id)).PublicationFence);

        await RecoverAsync(app, id);

        var completed = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, completed.Status);
        Assert.NotNull(completed.PublicationFence);
        Assert.Equal(completed.FileHash, await new FileHashService().ComputeHashAsync(completed.DestinationPath!));
        Assert.Equal("unconfirmed legacy bytes", await File.ReadAllTextAsync(legacy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_the_completion_commit_preserves_a_replayable_import(bool afterRename)
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        var id = await app.SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelCommit = new CancelCompletion(cancellation);
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            if (afterRename)
                services.AddDbContext<AppDbContext>(options => options.AddInterceptors(cancelCommit));
            else
            {
                services.RemoveAll<IFileHashService>();
                services.AddSingleton<IFileHashService>(new CancelAfterStageHash(cancellation));
            }
        }));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(j => j.Candidates).SingleAsync(j => j.Id == id);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider
                .GetRequiredService<IEbookIngestionService>().AcceptCandidateAsync(job, job.Candidates.Single(), cancellation.Token));
        }
        var interrupted = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Publishing, interrupted.Status);
        Assert.Null(interrupted.AcceptedBook);
        Assert.True(File.Exists(interrupted.FilePath));
        Assert.Equal(afterRename, File.Exists(interrupted.DestinationPath!));
        using (var scope = host.Services.CreateScope())
        {
            var stage = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.SingleAsync();
            Assert.Equal(PublicationStageStatus.Owned, stage.Status);
            Assert.Equal(!afterRename, File.Exists(stage.FilePath));
        }

        await RecoverAsync(host, id);

        var completed = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, completed.Status);
        Assert.NotEqual(interrupted.PublicationFence, completed.PublicationFence);
        Assert.Equal(completed.FileHash, await new FileHashService().ComputeHashAsync(completed.DestinationPath!));
        Assert.Equal(completed.FileHash, await new FileHashService().ComputeHashAsync(completed.FilePath));
        using var verify = host.Services.CreateScope();
        var state = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await state.Books.CountAsync());
        Assert.All(await state.PublicationStages.ToListAsync(), stage => Assert.Equal(PublicationStageStatus.Cleaned, stage.Status));
    }

    [Fact]
    public async Task Maintenance_replays_a_deleted_stage_when_its_database_commit_failed()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var (job, stage) = await SeedStageAsync(app, PublicationStageStatus.Owned);
        var failure = new CleanupCompletionFailure();
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(failure))));

        await Assert.ThrowsAsync<DbUpdateException>(() => MaintainAsync(host));

        Assert.True(failure.ObservedDeletion);
        Assert.False(File.Exists(stage.FilePath));
        using (var scope = host.Services.CreateScope())
        {
            var interrupted = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.SingleAsync();
            Assert.Equal(PublicationStageStatus.Owned, interrupted.Status);
            Assert.Equal(0, interrupted.CleanupAttemptCount);
        }
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.FilePath));
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(stage.DestinationPath));

        await MaintainAsync(host);

        using var verify = host.Services.CreateScope();
        var settled = await verify.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.SingleAsync();
        Assert.Equal(PublicationStageStatus.Cleaned, settled.Status);
        Assert.Equal(1, settled.CleanupAttemptCount);
        Assert.Null(settled.CleanupError);
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.FilePath));
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(stage.DestinationPath));
    }

    [Fact]
    public async Task Maintenance_settles_a_missing_planned_file_without_claiming_ownership()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var (_, stage) = await SeedStageAsync(app, PublicationStageStatus.Planned);
        Assert.False(File.Exists(stage.FilePath));

        await MaintainAsync(app);

        using var verify = app.Services.CreateScope();
        var settled = await verify.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.SingleAsync();
        Assert.Equal(PublicationStageStatus.Cleaned, settled.Status);
        Assert.Equal(1, settled.CleanupAttemptCount);
        Assert.Null(settled.CleanupAfter);
        Assert.Null(settled.CleanupError);
    }

    [Fact]
    public async Task Maintenance_retains_an_owned_file_whose_path_does_not_match_its_stage_token()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var (job, stage) = await SeedStageAsync(app, PublicationStageStatus.Owned);
        var mismatch = Path.Combine(app.Library, "unrelated.partial");
        File.Move(stage.FilePath, mismatch);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tracked = await db.PublicationStages.SingleAsync();
            tracked.FilePath = mismatch;
            await db.SaveChangesAsync();
        }

        await MaintainAsync(app);

        Assert.Equal("abandoned partial bytes", await File.ReadAllTextAsync(mismatch));
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.FilePath));
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(stage.DestinationPath));
        using var verify = app.Services.CreateScope();
        var retained = await verify.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.SingleAsync();
        Assert.Equal(PublicationStageStatus.Owned, retained.Status);
        Assert.Equal(1, retained.CleanupAttemptCount);
        Assert.NotNull(retained.CleanupError);
        Assert.True(retained.CleanupAfter > retained.UpdatedAt);
    }

    private static async Task<(ImportJob Job, PublicationStage Stage)> SeedStageAsync(PaparrFactory app, PublicationStageStatus status)
    {
        var job = await app.ReadAsync(await app.SeedAsync(ImportStatus.Failed));
        var destination = Path.Combine(app.Library, "published.pdf");
        File.Copy(job.FilePath, destination);
        var id = Guid.NewGuid();
        var stage = new PublicationStage
        {
            Id = id, ImportJobId = job.Id, PublicationFence = Guid.NewGuid(), DestinationPath = destination,
            FilePath = destination + "." + id.ToString("N") + ".partial", FileHash = job.FileHash, Status = status,
            CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };
        if (status == PublicationStageStatus.Owned) await File.WriteAllTextAsync(stage.FilePath, "abandoned partial bytes");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PublicationStages.Add(stage);
        await db.SaveChangesAsync();
        return (job, stage);
    }

    private static async Task MaintainAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StagingService>().RunBatchAsync(default);
    }

    private static async Task RecoverAsync(WebApplicationFactory<Program> app, long id)
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(j => j.Candidates).SingleAsync(j => j.Id == id);
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IEbookIngestionService>().RecoverAsync(job);
        }
        using var cleanup = app.Services.CreateScope();
        await cleanup.ServiceProvider.GetRequiredService<StagingService>().RunBatchAsync(default);
    }

    private sealed class CancelCompletion(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private bool armed = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (armed && eventData.Context!.ChangeTracker.Entries<ImportJob>().Any(e =>
                e.State == EntityState.Modified && e.Entity.Status == ImportStatus.Completed))
            {
                armed = false;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CleanupCompletionFailure : SaveChangesInterceptor
    {
        private bool armed = true;
        public bool ObservedDeletion { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var cleaned = eventData.Context!.ChangeTracker.Entries<PublicationStage>().FirstOrDefault(e =>
                e.State == EntityState.Modified && e.Entity.Status == PublicationStageStatus.Cleaned);
            if (armed && cleaned is not null)
            {
                armed = false;
                ObservedDeletion = !File.Exists(cleaned.Entity.FilePath);
                throw new DbUpdateException("Injected maintenance completion failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelAfterStageHash(CancellationTokenSource cancellation) : IFileHashService
    {
        private bool armed = true;
        public async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
        {
            var hash = await new FileHashService().ComputeHashAsync(path, cancellationToken);
            if (armed && path.EndsWith(".partial", StringComparison.Ordinal))
            {
                armed = false;
                cancellation.Cancel();
            }
            return hash;
        }
    }
}
