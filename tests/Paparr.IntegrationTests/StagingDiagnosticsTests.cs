using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Authentication;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class StagingDiagnosticsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Counts_distinguish_current_attempts_and_do_not_disclose_paths_or_errors()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        var id = await app.SeedAsync(ImportStatus.Publishing);
        var fence = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.FindAsync(id);
            job!.PublicationFence = fence;
            var statuses = new[] { PublicationStageStatus.Planned, PublicationStageStatus.Owned,
                PublicationStageStatus.Planned, PublicationStageStatus.Owned, PublicationStageStatus.Published,
                PublicationStageStatus.Retained, PublicationStageStatus.Cleaned };
            for (var i = 0; i < statuses.Length; i++)
            {
                db.PublicationStages.Add(new PublicationStage
                {
                    Id = Guid.NewGuid(), ImportJobId = id, PublicationFence = i < 2 ? fence : Guid.NewGuid(),
                    FilePath = Path.Combine(app.Library, $"private-{i}.partial"),
                    DestinationPath = Path.Combine(app.Library, "private.pdf"), FileHash = job.FileHash,
                    Status = statuses[i], CleanupError = "private error with credentials", CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/diagnostics/staging");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(new StagingStatusDto(2, 3, 1, 1), await response.Content.ReadFromJsonAsync<StagingStatusDto>());
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(app.Root, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fence.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Counts_require_the_owner_session_when_authentication_is_enabled()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        const string password = "temporary staging diagnostics owner password";
        app.SettingsOverrides["Authentication:Enabled"] = "true";
        app.SettingsOverrides["Authentication:PasswordHash"] = OwnerPassword.Hash(password);
        using var client = app.CreateClient();
        using var anonymous = await client.GetAsync("/api/diagnostics/staging");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var session = await client.GetFromJsonAsync<SessionResponse>("/api/auth/session");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { password })
        };
        loginRequest.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", session!.CsrfToken);
        using var login = await client.SendAsync(loginRequest);
        login.EnsureSuccessStatusCode();
        using var response = await client.GetAsync("/api/diagnostics/staging");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(new StagingStatusDto(0, 0, 0, 0), await response.Content.ReadFromJsonAsync<StagingStatusDto>());
    }

    private sealed record SessionResponse(string CsrfToken);
}
