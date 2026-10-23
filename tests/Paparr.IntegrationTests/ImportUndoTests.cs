using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paparr.API.Authentication;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class ImportUndoTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Undo_returns_to_review_without_rediscovery_and_old_operation_replay_preserves_a_reapproved_book()
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        var bytes = await File.ReadAllBytesAsync(imported.FilePath);
        using var previewResponse = await client.GetAsync($"/api/imports/{imported.Id}/undo-preview");
        previewResponse.EnsureSuccessStatusCode();
        Assert.True(previewResponse.Headers.CacheControl?.NoStore);
        var body = await previewResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(app.Root, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(app.Root.Replace("\\", "\\\\"), body, StringComparison.OrdinalIgnoreCase);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<UndoPreviewDto>())!;
        Assert.True(preview.CanUndo);
        Assert.False(Path.IsPathRooted(preview.SourceRelativePath));
        Assert.False(Path.IsPathRooted(preview.LibraryRelativePath));
        var operationId = Guid.NewGuid();

        using var undo = await StartAsync(client, imported.Id, preview.Revision, operationId);
        undo.EnsureSuccessStatusCode();
        var undone = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.AwaitingApproval, undone.Status);
        Assert.Null(undone.AcceptedBook);
        Assert.False(File.Exists(imported.DestinationPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(imported.FilePath));
        await ScanAsync(app);
        var stillReviewing = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.AwaitingApproval, stillReviewing.Status);
        Assert.Equal(undone.Revision, stillReviewing.Revision);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.ImportJobs.CountAsync());
            Assert.Equal(0, await db.Books.CountAsync());
        }

        using var reapproval = await client.PostAsync($"/api/imports/{imported.Id}/accept/{undone.Candidates.Single().Id}", null);
        reapproval.EnsureSuccessStatusCode();
        var reimported = await app.ReadAsync(imported.Id);
        Assert.NotEqual(imported.AcceptedBook!.Id, reimported.AcceptedBook!.Id);
        using var repeated = await StartAsync(client, imported.Id, preview.Revision, operationId);
        repeated.EnsureSuccessStatusCode();
        var afterReplay = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.Completed, afterReplay.Status);
        Assert.Equal(reimported.AcceptedBook.Id, afterReplay.AcceptedBook!.Id);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(afterReplay.DestinationPath!));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(afterReplay.FilePath));
        using var verify = app.Services.CreateScope();
        var state = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await state.Books.CountAsync());
        Assert.Equal(1, await state.ImportUndos.CountAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Files_changed_or_removed_after_preview_are_not_touched_by_undo(bool source, bool missing)
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        var preview = await PreviewAsync(client, imported.Id);
        Assert.True(preview.CanUndo);
        var affected = source ? imported.FilePath : imported.DestinationPath!;
        var unaffected = source ? imported.DestinationPath! : imported.FilePath;
        var original = await File.ReadAllBytesAsync(unaffected);
        if (missing) File.Delete(affected);
        else await File.WriteAllTextAsync(affected, "replacement content that must survive");

        using var response = await StartAsync(client, imported.Id, preview.Revision, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(app.Root, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(app.Root.Replace("\\", "\\\\"), body, StringComparison.OrdinalIgnoreCase);
        var after = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.Completed, after.Status);
        Assert.Equal(imported.AcceptedBook!.Id, after.AcceptedBook!.Id);
        Assert.Equal(original, await File.ReadAllBytesAsync(unaffected));
        if (missing) Assert.False(File.Exists(affected));
        else Assert.Equal("replacement content that must survive", await File.ReadAllTextAsync(affected));
        using var scope = app.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImportUndos.CountAsync());
    }

    [Fact]
    public async Task A_failed_request_commit_leaves_both_copies_and_the_book_unchanged()
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        var preview = await PreviewAsync(client, imported.Id);
        var failure = new UndoCommitFailure { FailRequest = true };
        using var host = WithInterceptor(app, failure);
        using var failingClient = host.CreateClient();

        using var response = await StartAsync(failingClient, imported.Id, preview.Revision, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(failure.FailRequest);
        var after = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.Completed, after.Status);
        Assert.Equal(imported.AcceptedBook!.Id, after.AcceptedBook!.Id);
        await AssertBothHashesAsync(imported);
        using var verify = app.Services.CreateScope();
        Assert.Equal(0, await verify.ServiceProvider.GetRequiredService<AppDbContext>().ImportUndos.CountAsync());
    }

    [Fact]
    public async Task Completion_failure_after_deletion_replays_from_the_durable_operation()
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        var preview = await PreviewAsync(client, imported.Id);
        var failure = new UndoCommitFailure { FailCompletion = true };
        using var host = WithInterceptor(app, failure);
        using var failingClient = host.CreateClient();
        var operationId = Guid.NewGuid();

        using var response = await StartAsync(failingClient, imported.Id, preview.Revision, operationId);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(failure.FailCompletion);
        Assert.False(File.Exists(imported.DestinationPath));
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(imported.FilePath));
        var interrupted = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.Undoing, interrupted.Status);
        Assert.NotNull(interrupted.AcceptedBook);
        await ExpireUndoAsync(app, operationId);
        await RecoverUndoAsync(app);
        using var replay = await StartAsync(client, imported.Id, preview.Revision, operationId);
        replay.EnsureSuccessStatusCode();
        var recovered = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.AwaitingApproval, recovered.Status);
        Assert.Null(recovered.AcceptedBook);
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(imported.FilePath));
        Assert.False(File.Exists(imported.DestinationPath));
        using var verify = app.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Books.CountAsync());
        Assert.Equal(ImportUndoStatus.Completed, (await db.ImportUndos.SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancellation_after_the_request_commit_is_recovered_in_a_new_scope()
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        var preview = await PreviewAsync(client, imported.Id);
        var gate = new HashGate(imported.DestinationPath!, 2);
        using var host = WithHashGate(app, gate);
        using var cancellation = new CancellationTokenSource();
        using var scope = host.Services.CreateScope();
        var operationId = Guid.NewGuid();
        var running = scope.ServiceProvider.GetRequiredService<ImportUndoService>()
            .StartAsync(imported.Id, preview.Revision, operationId, cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ImportStatus.Undoing, (await app.ReadAsync(imported.Id)).Status);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        finally
        {
            cancellation.Cancel();
            gate.Release.TrySetResult();
            try { await running.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (OperationCanceledException) { }
        }
        await AssertBothHashesAsync(imported);
        await ExpireUndoAsync(app, operationId);
        await RecoverUndoAsync(app);
        var recovered = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.AwaitingApproval, recovered.Status);
        Assert.Null(recovered.AcceptedBook);
        Assert.False(File.Exists(imported.DestinationPath));
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(imported.FilePath));
    }

    [Fact]
    public async Task Cleanup_that_already_owns_the_deletion_lock_prevents_undo_from_removing_the_other_copy()
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        await RequestCleanupAsync(app, imported.Id);
        var preview = await PreviewAsync(client, imported.Id);
        var gate = new HashGate(imported.DestinationPath!, 1);
        var locks = new ParentLockObserver();
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileHashService>();
            services.AddScoped<IFileHashService>(_ => gate);
            services.Configure<IngestionOptions>(options => options.DeleteSourceAfterImport = true);
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(locks));
        }));
        using var competingClient = host.CreateClient();
        using var cleanupScope = host.Services.CreateScope();
        var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await cleanupDb.ImportJobs.SingleAsync(j => j.Id == imported.Id);
        var cleanup = cleanupScope.ServiceProvider.GetRequiredService<SourceCleanupService>().TryCleanupAsync(job, default);
        Task<HttpResponseMessage>? undo = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            undo = StartAsync(competingClient, imported.Id, preview.Revision, Guid.NewGuid());
            await locks.SecondLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            gate.Release.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
            using var response = await undo.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            gate.Release.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
            if (undo is not null) using (await undo.WaitAsync(TimeSpan.FromSeconds(30))) { }
        }
        var after = await app.ReadAsync(imported.Id);
        Assert.Equal(ImportStatus.Completed, after.Status);
        Assert.NotNull(after.AcceptedBook);
        Assert.False(File.Exists(imported.FilePath));
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(imported.DestinationPath!));
    }

    [Fact]
    public async Task An_admitted_undo_stops_previously_claimed_cleanup_and_new_integrity_scans()
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        await RequestCleanupAsync(app, imported.Id);
        var gate = new HashGate(imported.DestinationPath!, 2);
        var claimGate = new CleanupClaimGate();
        var locks = new ParentLockObserver(3);
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileHashService>();
            services.AddScoped<IFileHashService>(_ => gate);
            services.Configure<IngestionOptions>(options => options.DeleteSourceAfterImport = true);
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(claimGate, locks));
        }));
        using var competingClient = host.CreateClient();
        using var cleanupScope = host.Services.CreateScope();
        var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var staleJob = await cleanupDb.ImportJobs.SingleAsync(j => j.Id == imported.Id);
        var cleanup = cleanupScope.ServiceProvider.GetRequiredService<SourceCleanupService>().TryCleanupAsync(staleJob, default);
        Task<HttpResponseMessage>? undo = null;
        try
        {
            await claimGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var preview = await PreviewAsync(client, imported.Id);
            undo = StartAsync(competingClient, imported.Id, preview.Revision, Guid.NewGuid());
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ImportStatus.Undoing, (await app.ReadAsync(imported.Id)).Status);
            using var audit = await competingClient.PostAsync("/api/library/audits", null);
            Assert.Equal(HttpStatusCode.Conflict, audit.StatusCode);
            claimGate.Release.TrySetResult();
            await locks.SecondLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            gate.Release.TrySetResult();
            using var response = await undo.WaitAsync(TimeSpan.FromSeconds(30));
            response.EnsureSuccessStatusCode();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            claimGate.Release.TrySetResult();
            gate.Release.TrySetResult();
            if (undo is not null) using (await undo.WaitAsync(TimeSpan.FromSeconds(30))) { }
            await cleanup.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(imported.Id)).Status);
        Assert.False(File.Exists(imported.DestinationPath));
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(imported.FilePath));
        using var allowedAudit = await competingClient.PostAsync("/api/library/audits", null);
        Assert.Equal(HttpStatusCode.Accepted, allowedAudit.StatusCode);
    }

    [Fact]
    public async Task An_active_integrity_scan_rejects_undo_and_preserves_both_copies()
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        var preview = await PreviewAsync(client, imported.Id);
        using var audit = await client.PostAsync("/api/library/audits", null);
        Assert.Equal(HttpStatusCode.Accepted, audit.StatusCode);

        using var undo = await StartAsync(client, imported.Id, preview.Revision, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, undo.StatusCode);
        Assert.Equal(ImportStatus.Completed, (await app.ReadAsync(imported.Id)).Status);
        await AssertBothHashesAsync(imported);
    }

    [Fact]
    public async Task Concurrent_audit_admission_waits_for_the_uncommitted_undo_request()
    {
        using var app = await RetainedFactoryAsync();
        using var client = app.CreateClient();
        var imported = await ImportAsync(app, client);
        var preview = await PreviewAsync(client, imported.Id);
        var requestGate = new UndoAdmissionGate();
        var hashGate = new HashGate(imported.DestinationPath!, 2);
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileHashService>();
            services.AddScoped<IFileHashService>(_ => hashGate);
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(requestGate, new AdmissionLockObserver(requestGate)));
        }));
        using var competingClient = host.CreateClient();
        var undo = StartAsync(competingClient, imported.Id, preview.Revision, Guid.NewGuid());
        Task<HttpResponseMessage>? audit = null;
        try
        {
            await requestGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            audit = competingClient.PostAsync("/api/library/audits", null);
            await requestGate.SecondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(30));
            requestGate.Release.TrySetResult();
            await hashGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var auditResponse = await audit.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.Conflict, auditResponse.StatusCode);
            hashGate.Release.TrySetResult();
            using var undoResponse = await undo.WaitAsync(TimeSpan.FromSeconds(30));
            undoResponse.EnsureSuccessStatusCode();
        }
        finally
        {
            requestGate.Release.TrySetResult();
            hashGate.Release.TrySetResult();
            using (await undo.WaitAsync(TimeSpan.FromSeconds(30))) { }
            if (audit is not null) using (await audit.WaitAsync(TimeSpan.FromSeconds(30))) { }
        }
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(imported.Id)).Status);
        Assert.False(File.Exists(imported.DestinationPath));
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(imported.FilePath));
    }

    [Fact]
    public async Task Undo_requires_owner_access_and_a_csrf_token()
    {
        using var app = await RetainedFactoryAsync();
        const string password = "undo owner integration password";
        app.SettingsOverrides["Authentication:Enabled"] = "true";
        app.SettingsOverrides["Authentication:PasswordHash"] = OwnerPassword.Hash(password);
        app.SettingsOverrides["Authentication:SecureCookies"] = "false";
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(j => j.Candidates).SingleAsync(j => j.Id == id);
            await scope.ServiceProvider.GetRequiredService<IEbookIngestionService>().AcceptCandidateAsync(job, job.Candidates.Single());
        }
        var imported = await app.ReadAsync(id);
        using var anonymousPreview = await client.GetAsync($"/api/imports/{id}/undo-preview");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPreview.StatusCode);
        using var anonymousUndo = await StartAsync(client, id, imported.Revision, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousUndo.StatusCode);
        var session = (await client.GetFromJsonAsync<SessionResponse>("/api/auth/session"))!;
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { password }) };
        loginRequest.Headers.Add("X-CSRF-TOKEN", session.CsrfToken);
        using var login = await client.SendAsync(loginRequest);
        login.EnsureSuccessStatusCode();
        var signedIn = (await login.Content.ReadFromJsonAsync<SessionResponse>())!;
        var preview = await PreviewAsync(client, id);
        Assert.True(preview.CanUndo);
        using var noCsrf = await StartAsync(client, id, preview.Revision, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        await AssertBothHashesAsync(imported);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", signedIn.CsrfToken);
        using var authorized = await StartAsync(client, id, preview.Revision, Guid.NewGuid());
        authorized.EnsureSuccessStatusCode();
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(id)).Status);
        Assert.Equal(imported.FileHash, await new FileHashService().ComputeHashAsync(imported.FilePath));
        Assert.False(File.Exists(imported.DestinationPath));
    }

    private async Task<PaparrFactory> RetainedFactoryAsync()
    {
        var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["DeleteSourceAfterImport"] = "false";
        return app;
    }

    private static async Task<ImportJob> ImportAsync(PaparrFactory app, HttpClient client)
    {
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);
        using var response = await client.PostAsync($"/api/imports/{id}/accept/{before.Candidates.Single().Id}", null);
        response.EnsureSuccessStatusCode();
        var imported = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.Completed, imported.Status);
        await AssertBothHashesAsync(imported);
        return imported;
    }

    private static async Task AssertBothHashesAsync(ImportJob job)
    {
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.FilePath));
        Assert.Equal(job.FileHash, await new FileHashService().ComputeHashAsync(job.DestinationPath!));
    }

    private static async Task<UndoPreviewDto> PreviewAsync(HttpClient client, long id) =>
        (await client.GetFromJsonAsync<UndoPreviewDto>($"/api/imports/{id}/undo-preview"))!;

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, long id, Guid revision, Guid operationId) =>
        client.PostAsJsonAsync($"/api/imports/{id}/undo", new { revision, operationId });

    private static WebApplicationFactory<Program> WithInterceptor(PaparrFactory app, IInterceptor interceptor) =>
        app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(interceptor))));

    private static WebApplicationFactory<Program> WithHashGate(PaparrFactory app, HashGate gate) =>
        app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileHashService>();
            services.AddScoped<IFileHashService>(_ => gate);
        }));

    private static async Task ExpireUndoAsync(PaparrFactory app, Guid operationId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var operation = await db.ImportUndos.SingleAsync(undo => undo.Id == operationId);
        operation.RetryAfter = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private static async Task RecoverUndoAsync(PaparrFactory app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ImportUndoService>().RunBatchAsync(default);
    }

    private static async Task RequestCleanupAsync(PaparrFactory app, long id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ImportJobs.SingleAsync(j => j.Id == id);
        job.CleanupStatus = SourceCleanupStatus.Pending;
        job.CleanupAfter = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private static async Task ScanAsync(PaparrFactory app)
    {
        var clock = new MutableClock();
        var stability = new FileStabilityTracker(clock);
        foreach (var path in Directory.GetFiles(app.Ingest, "*.pdf", SearchOption.AllDirectories))
            stability.IsReady(new FileInfo(path), TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(6));
        using var scope = app.Services.CreateScope();
        var scanner = new ImportScanner(scope.ServiceProvider.GetRequiredService<AppDbContext>(), new FileHashService(), stability,
            new StableFileHashCache(clock), Options.Create(new IngestionOptions { IngestPath = app.Ingest, LibraryPath = app.Library }),
            clock, NullLogger<ImportScanner>.Instance);
        await scanner.ScanAsync(default);
        await scanner.ScanAsync(default);
    }

    private sealed record SessionResponse(bool Enabled, bool Authenticated, string CsrfToken);

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class HashGate(string path, int blockOnCall) : IFileHashService
    {
        private int calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> ComputeHashAsync(string candidate, CancellationToken cancellationToken = default)
        {
            if (candidate.Equals(path, StringComparison.OrdinalIgnoreCase) && Interlocked.Increment(ref calls) == blockOnCall)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await new FileHashService().ComputeHashAsync(candidate, cancellationToken);
        }
    }

    private sealed class UndoCommitFailure : SaveChangesInterceptor
    {
        public bool FailRequest { get; set; }
        public bool FailCompletion { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var operations = eventData.Context!.ChangeTracker.Entries<ImportUndo>();
            if (FailRequest && operations.Any(entry => entry.State == EntityState.Added))
            {
                FailRequest = false;
                throw new DbUpdateException("Injected undo request persistence failure.");
            }
            if (FailCompletion && operations.Any(entry => entry.State == EntityState.Modified && entry.Entity.Status == ImportUndoStatus.Completed))
            {
                FailCompletion = false;
                throw new DbUpdateException("Injected undo completion persistence failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ParentLockObserver(int notifyOnCall = 2) : DbCommandInterceptor
    {
        private int calls;
        public TaskCompletionSource SecondLockRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) && Interlocked.Increment(ref calls) == notifyOnCall)
                SecondLockRequested.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CleanupClaimGate : SaveChangesInterceptor
    {
        private int seen;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ImportJob>().Any(entry =>
                entry.Entity.Status == ImportStatus.Completed && entry.Entity.CleanupStatus == SourceCleanupStatus.Pending &&
                entry.Entity.CleanupAttemptCount > 0) && Interlocked.Exchange(ref seen, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class UndoAdmissionGate : SaveChangesInterceptor
    {
        public int Admissions;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ImportUndo>().Any(entry => entry.State == EntityState.Added))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class AdmissionLockObserver(UndoAdmissionGate gate) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) &&
                Interlocked.Increment(ref gate.Admissions) == 2)
                gate.SecondAdmission.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
