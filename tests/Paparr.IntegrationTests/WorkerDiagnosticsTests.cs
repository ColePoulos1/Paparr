using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Authentication;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class WorkerDiagnosticsTests(PostgresFixture postgres)
{
    private const string Password = "worker diagnostics owner password";

    [Fact]
    public async Task Endpoint_is_protected_no_store_and_never_exposes_source_paths()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var secretPath = Path.Combine(app.Root, "private-library", "reader.pdf");
        app.SettingsOverrides["Authentication:Enabled"] = "true";
        app.SettingsOverrides["Authentication:PasswordHash"] = OwnerPassword.Hash(Password);
        app.SettingsOverrides["Authentication:KeysPath"] = Path.Combine(app.Root, "keys");
        using var client = app.CreateClient();

        using var anonymous = await client.GetAsync("/api/diagnostics/worker");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ImportJobs.Add(new ImportJob
        {
            FilePath = secretPath,
            FileHash = new string('A', 64),
            Status = ImportStatus.Failed,
            LastError = secretPath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/auth/session");
        Assert.NotNull(session);
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { password = Password })
        };
        loginRequest.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", session.CsrfToken);
        using var login = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var response = await client.GetAsync("/api/diagnostics/worker");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretPath, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(app.Root, body, StringComparison.OrdinalIgnoreCase);
        var diagnostics = await response.Content.ReadFromJsonAsync<WorkerDiagnosticsSnapshot>();
        Assert.NotNull(diagnostics);
        Assert.False(diagnostics.Enabled);
        Assert.Equal("Disabled", diagnostics.State);
        Assert.Equal(["queuedWork", "sourceCleanup", "libraryAudit", "discovery"],
            diagnostics.Stages.Select(stage => stage.Key));
        Assert.All(diagnostics.Stages, stage => Assert.Equal("NotRun", stage.State));
    }

    [Fact]
    public async Task Explicit_worker_cycle_records_all_four_stages()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var worker = ActivatorUtilities.CreateInstance<BackgroundIngestionWorker>(app.Services);

        await worker.RunCycleAsync(default);

        using var response = await client.GetAsync("/api/diagnostics/worker");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var diagnostics = await response.Content.ReadFromJsonAsync<WorkerDiagnosticsSnapshot>();
        Assert.NotNull(diagnostics);
        Assert.Equal("Disabled", diagnostics.State);
        Assert.Equal(["queuedWork", "sourceCleanup", "libraryAudit", "discovery"],
            diagnostics.Stages.Select(stage => stage.Key));
        Assert.All(diagnostics.Stages, stage => Assert.Equal("Succeeded", stage.State));
        Assert.All(diagnostics.Stages, stage => Assert.NotNull(stage.LastAttemptAt));
        Assert.All(diagnostics.Stages, stage => Assert.NotNull(stage.LastSuccessAt));
    }

    [Fact]
    public async Task Discovery_failure_is_reported_with_sanitized_actionable_error()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var worker = ActivatorUtilities.CreateInstance<BackgroundIngestionWorker>(app.Services);
        Directory.Delete(app.Ingest);
        await File.WriteAllTextAsync(app.Ingest, "not a directory");

        await Assert.ThrowsAnyAsync<IOException>(() => worker.RunCycleAsync(default));

        using var response = await client.GetAsync("/api/diagnostics/worker");
        response.EnsureSuccessStatusCode();
        var diagnostics = await response.Content.ReadFromJsonAsync<WorkerDiagnosticsSnapshot>();
        Assert.NotNull(diagnostics);
        Assert.Equal("Disabled", diagnostics.State);
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded", "Failed"],
            diagnostics.Stages.Select(stage => stage.State));
        Assert.Equal("A required file or directory could not be accessed. Check the path and permissions.",
            diagnostics.Stages[3].Error);
        Assert.DoesNotContain(app.Root, diagnostics.Stages[3].Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SessionResponse(bool Enabled, bool Authenticated, string CsrfToken);
}
