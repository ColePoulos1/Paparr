using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Authentication;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class AuthenticationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery staple";
    private const string WrongPassword = "not the owner password";

    [Fact]
    public async Task Login_requires_csrf_and_authorized_mutations_require_the_session_token()
    {
        using var app = await CreateAuthenticatedFactoryAsync(postgres);
        using var anonymous = app.CreateClient();
        var importId = await app.SeedAsync();
        var before = await app.ReadAsync(importId);

        var session = await GetSessionAsync(anonymous);
        Assert.True(session.Enabled);
        Assert.False(session.Authenticated);
        Assert.False(string.IsNullOrWhiteSpace(session.CsrfToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/imports")).StatusCode);
        using (var protectedMutation = await PostAcceptAsync(anonymous, app, importId))
            Assert.Equal(HttpStatusCode.Unauthorized, protectedMutation.StatusCode);
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(importId)).Status);
        Assert.True(File.Exists(before.FilePath));

        using (var missingCsrf = await LoginAsync(anonymous, Password))
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using (var badPassword = await LoginAsync(anonymous, WrongPassword, session.CsrfToken))
            Assert.Equal(HttpStatusCode.Unauthorized, badPassword.StatusCode);

        using var login = await LoginAsync(anonymous, Password, session.CsrfToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authenticated = await login.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.NotNull(authenticated);
        Assert.True(authenticated.Authenticated);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/imports")).StatusCode);
        using (var missingMutationToken = await PostAcceptAsync(anonymous, app, importId))
            Assert.Equal(HttpStatusCode.BadRequest, missingMutationToken.StatusCode);
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(importId)).Status);
        Assert.True(File.Exists(before.FilePath));

        using (var validMutation = await PostAcceptAsync(anonymous, app, importId, authenticated.CsrfToken))
            Assert.True(validMutation.IsSuccessStatusCode);
        Assert.Equal(ImportStatus.Completed, (await app.ReadAsync(importId)).Status);
        Assert.False(File.Exists(before.FilePath));
    }

    [Fact]
    public async Task Logout_revokes_the_server_session_and_a_captured_cookie_cannot_be_replayed()
    {
        using var app = await CreateAuthenticatedFactoryAsync(postgres);
        using var client = app.CreateClient();
        var anonymousSession = await GetSessionAsync(client);
        using var login = await LoginAsync(client, Password, anonymousSession.CsrfToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = CaptureSessionCookie(login);
        var signedIn = await login.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.NotNull(signedIn);

        using (var logout = await LogoutAsync(client, signedIn.CsrfToken))
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var afterLogout = await GetSessionAsync(client);
        Assert.False(afterLogout.Authenticated);

        using var replay = app.CreateClient();
        AddCookie(replay, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/imports")).StatusCode);
    }

    [Fact]
    public async Task Expired_persisted_session_is_rejected()
    {
        using var app = await CreateAuthenticatedFactoryAsync(postgres);
        using var client = app.CreateClient();
        var anonymousSession = await GetSessionAsync(client);
        using var login = await LoginAsync(client, Password, anonymousSession.CsrfToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = CaptureSessionCookie(login);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"AuthSessions\" SET \"ExpiresAt\" = NOW() - INTERVAL '1 minute'");
        }

        using var replay = app.CreateClient();
        AddCookie(replay, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/imports")).StatusCode);
    }

    [Fact]
    public async Task Session_survives_a_new_factory_when_database_and_data_protection_keys_are_shared()
    {
        var sharedKeys = Path.Combine(Path.GetTempPath(), "paparr-auth-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedKeys);
        try
        {
            string connection;
            string passwordHash;
            string cookie;
            using (var first = await CreateAuthenticatedFactoryAsync(postgres, sharedKeys))
            {
                connection = first.ConnectionString;
                passwordHash = first.SettingsOverrides["Authentication:PasswordHash"]!;
                using var client = first.CreateClient();
                var session = await GetSessionAsync(client);
                using var login = await LoginAsync(client, Password, session.CsrfToken);
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                cookie = CaptureSessionCookie(login);
            }

            using var restarted = new PaparrFactory(connection);
            ConfigureAuthentication(restarted, passwordHash, sharedKeys);
            Directory.CreateDirectory(restarted.Ingest);
            Directory.CreateDirectory(restarted.Library);
            using var afterRestart = restarted.CreateClient();
            AddCookie(afterRestart, cookie);
            Assert.Equal(HttpStatusCode.OK, (await afterRestart.GetAsync("/api/imports")).StatusCode);
        }
        finally
        {
            if (Directory.Exists(sharedKeys)) Directory.Delete(sharedKeys, recursive: true);
        }
    }

    [Fact]
    public async Task Changing_the_configured_owner_hash_invalidates_an_existing_session()
    {
        var sharedKeys = Path.Combine(Path.GetTempPath(), "paparr-auth-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedKeys);
        try
        {
            string connection;
            string cookie;
            using (var first = await CreateAuthenticatedFactoryAsync(postgres, sharedKeys))
            {
                connection = first.ConnectionString;
                using var client = first.CreateClient();
                var session = await GetSessionAsync(client);
                using var login = await LoginAsync(client, Password, session.CsrfToken);
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                cookie = CaptureSessionCookie(login);
            }

            using var changed = new PaparrFactory(connection);
            ConfigureAuthentication(changed, OwnerPassword.Hash("a different owner password"), sharedKeys);
            Directory.CreateDirectory(changed.Ingest);
            Directory.CreateDirectory(changed.Library);
            using var afterChange = changed.CreateClient();
            AddCookie(afterChange, cookie);
            Assert.Equal(HttpStatusCode.Unauthorized, (await afterChange.GetAsync("/api/imports")).StatusCode);
        }
        finally
        {
            if (Directory.Exists(sharedKeys)) Directory.Delete(sharedKeys, recursive: true);
        }
    }

    [Fact]
    public async Task Sixth_failed_login_in_one_minute_is_rate_limited()
    {
        using var app = await CreateAuthenticatedFactoryAsync(postgres);
        using var client = app.CreateClient();
        var session = await GetSessionAsync(client);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var failure = await LoginAsync(client, WrongPassword, session.CsrfToken);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }
        using var limited = await LoginAsync(client, WrongPassword, session.CsrfToken);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
    }

    [Fact]
    public async Task Secure_cookies_work_over_https_and_plain_http_cannot_start_sign_in()
    {
        using var app = await CreateAuthenticatedFactoryAsync(postgres);
        app.SettingsOverrides["Authentication:SecureCookies"] = "true";
        using var https = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var bootstrap = await https.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Assert.True(bootstrap.Headers.CacheControl?.NoStore);
        var csrfCookie = Assert.Single(bootstrap.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Paparr.Csrf="));
        AssertCookieFlags(csrfCookie);
        var anonymous = await bootstrap.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.NotNull(anonymous);
        using var login = await LoginAsync(https, Password, anonymous.CsrfToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        AssertCookieFlags(Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Paparr.Session=")));
        Assert.Equal(HttpStatusCode.OK, (await https.GetAsync("/api/imports")).StatusCode);

        using var http = app.CreateClient();
        using var insecure = await http.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.BadRequest, insecure.StatusCode);
        Assert.False(insecure.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
    }

    [Theory]
    [InlineData("10.42.0.17", 200)]
    [InlineData("10.43.0.17", 400)]
    public async Task Only_trusted_proxy_addresses_can_forward_the_https_scheme(string address, int expectedStatus)
    {
        using var app = await CreateAuthenticatedFactoryAsync(postgres);
        app.SettingsOverrides["Authentication:SecureCookies"] = "true";
        app.SettingsOverrides["ReverseProxy:TrustedNetworks"] = "10.42.0.0/24";
        var result = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            context.Request.Method = "GET";
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/api/auth/session";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        Assert.Equal(expectedStatus, result.Response.StatusCode);
    }

    private static void AssertCookieFlags(string cookie)
    {
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PaparrFactory> CreateAuthenticatedFactoryAsync(
        PostgresFixture postgres,
        string? keysPath = null)
    {
        var app = await PaparrFactory.CreateAsync(postgres);
        ConfigureAuthentication(app, OwnerPassword.Hash(Password), keysPath ?? Path.Combine(app.Root, "keys"));
        return app;
    }

    private static void ConfigureAuthentication(PaparrFactory app, string passwordHash, string keysPath)
    {
        app.SettingsOverrides["Authentication:Enabled"] = "true";
        app.SettingsOverrides["Authentication:PasswordHash"] = passwordHash;
        app.SettingsOverrides["Authentication:KeysPath"] = keysPath;
        app.SettingsOverrides["Authentication:SecureCookies"] = "false";
    }

    private static async Task<SessionResponse> GetSessionAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<SessionResponse>("/api/auth/session");
        Assert.NotNull(session);
        return session;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password, string? csrfToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { password })
        };
        if (csrfToken is not null)
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> LogoutAsync(HttpClient client, string csrfToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfToken);
        return client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAcceptAsync(
        HttpClient client,
        PaparrFactory app,
        long importId,
        string? csrfToken = null)
    {
        var job = await app.ReadAsync(importId);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/imports/{importId}/accept/{job.Candidates.Single().Id}");
        if (csrfToken is not null)
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfToken);
        return await client.SendAsync(request);
    }

    private static string CaptureSessionCookie(HttpResponseMessage response)
    {
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("Paparr.Session=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }

    private static void AddCookie(HttpClient client, string cookie)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
    }

    private sealed record SessionResponse(bool Enabled, bool Authenticated, string CsrfToken);
}
