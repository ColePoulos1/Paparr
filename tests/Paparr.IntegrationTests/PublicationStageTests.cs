using System.Data.Common;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class PublicationStageTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_expired_publisher_cannot_change_the_recovered_import(bool cleanAbandonedStage)
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        var clock = new MutableClock();
        var gate = new FirstStageHashGate();
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IFileHashService>();
            services.AddScoped<IFileHashService>(_ => gate);
        }));
        using var client = host.CreateClient();
        var oldRequest = client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null);
        try
        {
            var oldStagePath = await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var publishing = await app.ReadAsync(id);
            Assert.Equal(ImportStatus.Publishing, publishing.Status);
            Assert.NotNull(publishing.PublicationFence);
            Assert.True(File.Exists(oldStagePath));
            clock.Advance(TimeSpan.FromMinutes(61));

            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var job = await db.ImportJobs.Include(j => j.Candidates).SingleAsync(j => j.Id == id);
                await scope.ServiceProvider.GetRequiredService<IEbookIngestionService>().RecoverAsync(job);
            }

            var winner = await app.ReadAsync(id);
            Assert.Equal(ImportStatus.Completed, winner.Status);
            Assert.NotEqual(publishing.PublicationFence, winner.PublicationFence);
            Assert.Equal(before.FileHash, await new FileHashService().ComputeHashAsync(winner.DestinationPath!));
            var timestamp = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(winner.DestinationPath!, timestamp);
            if (cleanAbandonedStage)
            {
                await MaintainAsync(host);
                Assert.False(File.Exists(oldStagePath));
            }
            gate.Release.TrySetResult();
            using var response = await oldRequest.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict);

            var after = await app.ReadAsync(id);
            Assert.Equal(ImportStatus.Completed, after.Status);
            Assert.Null(after.LastError);
            Assert.Equal(winner.PublicationFence, after.PublicationFence);
            Assert.Equal(winner.AcceptedBook!.Id, after.AcceptedBook!.Id);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(after.DestinationPath!));
            Assert.Equal(before.FileHash, await new FileHashService().ComputeHashAsync(before.FilePath));
            Assert.Equal(before.FileHash, await new FileHashService().ComputeHashAsync(after.DestinationPath!));
            await MaintainAsync(host);
            using var verify = host.Services.CreateScope();
            Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<AppDbContext>().Books.CountAsync());
            Assert.Single(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
        }
        finally
        {
            gate.Release.TrySetResult();
            await oldRequest.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task A_failed_staging_plan_commit_creates_no_file()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        var failure = new PlanFailure();
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(failure))));
        using var client = host.CreateClient();
        failure.Armed = true;

        using var response = await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(failure.Armed);
        Assert.Empty(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
        Assert.Equal(before.FileHash, await new FileHashService().ComputeHashAsync(before.FilePath));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PublicationStages.ToListAsync());
        Assert.Empty(await db.Books.ToListAsync());
        Assert.Equal(ImportStatus.Publishing, (await app.ReadAsync(id)).Status);
    }

    [Fact]
    public async Task A_completion_commit_failure_replays_the_destination_and_reconciles_stage_records()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        app.Failure.Armed = true;
        using var response = await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var interrupted = await app.ReadAsync(id);
        Assert.True(File.Exists(before.FilePath));
        Assert.Equal(before.FileHash, await new FileHashService().ComputeHashAsync(interrupted.DestinationPath!));
        var timestamp = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(interrupted.DestinationPath!, timestamp);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.PublicationStages.ToListAsync());
            var job = await db.ImportJobs.Include(j => j.Candidates).SingleAsync(j => j.Id == id);
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IEbookIngestionService>().RecoverAsync(job);
        }
        await MaintainAsync(app);
        var recovered = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, recovered.Status);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(recovered.DestinationPath!));
        Assert.Equal(before.FileHash, await new FileHashService().ComputeHashAsync(recovered.DestinationPath!));
        Assert.False(File.Exists(before.FilePath));
        using var verify = app.Services.CreateScope();
        var state = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await state.Books.CountAsync());
        Assert.All(await state.PublicationStages.ToListAsync(), stage => Assert.Equal(PublicationStageStatus.Cleaned, stage.Status));
        Assert.Single(Directory.GetFiles(app.Library, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Maintenance_deletes_only_abandoned_confirmed_files_and_protects_the_active_fence()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var id = await app.SeedAsync(ImportStatus.Publishing);
        var job = await app.ReadAsync(id);
        var currentFence = Guid.NewGuid();
        var active = Stage(app, job, currentFence, "active.pdf", PublicationStageStatus.Owned);
        var abandoned = Stage(app, job, Guid.NewGuid(), "abandoned.pdf", PublicationStageStatus.Owned);
        var unconfirmed = Stage(app, job, Guid.NewGuid(), "unconfirmed.pdf", PublicationStageStatus.Planned);
        var outside = Stage(app, job, Guid.NewGuid(), "outside.pdf", PublicationStageStatus.Owned);
        outside.FilePath = Path.Combine(app.Root, Path.GetFileName(outside.FilePath));
        var unknown = Path.Combine(app.Library, "unrecorded." + Guid.NewGuid().ToString("N") + ".partial");
        foreach (var stage in new[] { active, abandoned, unconfirmed, outside })
        {
            await File.WriteAllTextAsync(stage.FilePath, stage.Id.ToString());
            File.SetLastWriteTimeUtc(stage.FilePath, DateTime.UtcNow.AddYears(-1));
        }
        await File.WriteAllTextAsync(unknown, "unrelated file");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tracked = await db.ImportJobs.SingleAsync(j => j.Id == id);
            tracked.PublicationFence = currentFence;
            tracked.LeaseExpiresAt = DateTime.UtcNow.AddDays(-1);
            db.PublicationStages.AddRange(active, abandoned, unconfirmed, outside);
            await db.SaveChangesAsync();
        }

        await MaintainAsync(app);
        await MaintainAsync(app);

        Assert.True(File.Exists(active.FilePath));
        Assert.False(File.Exists(abandoned.FilePath));
        Assert.Equal(unconfirmed.Id.ToString(), await File.ReadAllTextAsync(unconfirmed.FilePath));
        Assert.Equal(outside.Id.ToString(), await File.ReadAllTextAsync(outside.FilePath));
        Assert.Equal("unrelated file", await File.ReadAllTextAsync(unknown));
        Assert.True(File.Exists(job.FilePath));
        using var verify = app.Services.CreateScope();
        var records = await verify.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.ToDictionaryAsync(s => s.Id);
        Assert.Equal(0, records[active.Id].CleanupAttemptCount);
        Assert.Equal(PublicationStageStatus.Cleaned, records[abandoned.Id].Status);
        Assert.Equal(PublicationStageStatus.Retained, records[unconfirmed.Id].Status);
        Assert.NotNull(records[outside.Id].CleanupError);
    }

    [Fact]
    public async Task Maintenance_does_not_follow_a_reparse_directory_to_a_stage_target()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var id = await app.SeedAsync(ImportStatus.Failed);
        var job = await app.ReadAsync(id);
        var target = Path.Combine(app.Root, "keep-target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(app.Library, "linked-directory");
        await CreateDirectoryLinkAsync(link, target);
        try
        {
            var stage = Stage(app, job, Guid.NewGuid(), "linked-directory/book.pdf", PublicationStageStatus.Owned);
            await File.WriteAllTextAsync(stage.FilePath, "outside content");
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.PublicationStages.Add(stage);
                await db.SaveChangesAsync();
            }

            await MaintainAsync(app);

            Assert.Equal("outside content", await File.ReadAllTextAsync(Path.Combine(target, Path.GetFileName(stage.FilePath))));
            using var verify = app.Services.CreateScope();
            var retained = await verify.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.SingleAsync();
            Assert.NotEqual(PublicationStageStatus.Cleaned, retained.Status);
            Assert.NotNull(retained.CleanupError);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Independent_maintenance_scopes_serialize_cleanup_of_the_same_stage()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var id = await app.SeedAsync(ImportStatus.Failed);
        var stage = Stage(app, await app.ReadAsync(id), Guid.NewGuid(), "racing.pdf", PublicationStageStatus.Owned);
        await File.WriteAllTextAsync(stage.FilePath, "abandoned bytes");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PublicationStages.Add(stage);
            await db.SaveChangesAsync();
        }
        var gate = new CleanupGate();
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new CleanupSaveGate(gate), new CleanupLockGate(gate)))));
        var first = MaintainAsync(host);
        Task? second = null;
        try
        {
            await gate.FirstSaved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            second = MaintainAsync(host);
            await gate.SecondLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            gate.Release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            gate.Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(30));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.False(File.Exists(stage.FilePath));
        using var verify = app.Services.CreateScope();
        var cleaned = await verify.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.SingleAsync();
        Assert.Equal(PublicationStageStatus.Cleaned, cleaned.Status);
        Assert.Equal(1, cleaned.CleanupAttemptCount);
    }

    [Fact]
    public async Task A_locked_stage_is_retained_then_cleaned_after_the_handle_is_released()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix permits unlinking an open file.
        using var app = await PaparrFactory.CreateAsync(postgres);
        var id = await app.SeedAsync(ImportStatus.Failed);
        var stage = Stage(app, await app.ReadAsync(id), Guid.NewGuid(), "locked.pdf", PublicationStageStatus.Owned);
        await File.WriteAllTextAsync(stage.FilePath, "retry these bytes");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PublicationStages.Add(stage);
            await db.SaveChangesAsync();
        }
        using (File.Open(stage.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await MaintainAsync(app);
        Assert.Equal("retry these bytes", await File.ReadAllTextAsync(stage.FilePath));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = await db.PublicationStages.SingleAsync();
            Assert.NotNull(pending.CleanupError);
            Assert.NotNull(pending.CleanupAfter);
            pending.CleanupAfter = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await MaintainAsync(app);
        Assert.False(File.Exists(stage.FilePath));
        using var verify = app.Services.CreateScope();
        var cleaned = await verify.ServiceProvider.GetRequiredService<AppDbContext>().PublicationStages.SingleAsync();
        Assert.Equal(PublicationStageStatus.Cleaned, cleaned.Status);
        Assert.Equal(2, cleaned.CleanupAttemptCount);
        Assert.Null(cleaned.CleanupError);
    }

    private static PublicationStage Stage(PaparrFactory app, ImportJob job, Guid fence, string name, PublicationStageStatus status)
    {
        var id = Guid.NewGuid();
        var destination = Path.Combine(app.Library, name);
        return new PublicationStage
        {
            Id = id, ImportJobId = job.Id, PublicationFence = fence, DestinationPath = destination,
            FilePath = destination + "." + id.ToString("N") + ".partial", FileHash = job.FileHash, Status = status,
            CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };
    }

    private static async Task MaintainAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StagingService>().RunBatchAsync(default);
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        // Junctions exercise reparse traversal without requiring Windows symbolic-link privileges.
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class FirstStageHashGate : IFileHashService
    {
        private int seen;
        public TaskCompletionSource<string> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
        {
            if (path.EndsWith(".partial", StringComparison.Ordinal) && Interlocked.Increment(ref seen) == 1)
            {
                Entered.TrySetResult(path);
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await new FileHashService().ComputeHashAsync(path, cancellationToken);
        }
    }

    private sealed class PlanFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<PublicationStage>().Any(e => e.State == EntityState.Added))
            {
                Armed = false;
                throw new DbUpdateException("Injected staging plan failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CleanupGate
    {
        public int Locks;
        public int Saves;
        public TaskCompletionSource FirstSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondLockRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CleanupSaveGate(CleanupGate gate) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PublicationStage>().Any(e =>
                e.State == EntityState.Modified && e.Entity.Status == PublicationStageStatus.Cleaned) &&
                Interlocked.Increment(ref gate.Saves) == 1)
            {
                gate.FirstSaved.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class CleanupLockGate(CleanupGate gate) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) && Interlocked.Increment(ref gate.Locks) == 2)
                gate.SecondLockRequested.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
