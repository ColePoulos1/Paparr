using System.Net;
using Paparr.API.Domain;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class BrowserRequestTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Cross_site_approval_is_rejected_without_changing_the_import_or_source()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);

        using var response = await AcceptAsync(client, app, id, request =>
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await app.ReadAsync(id);
        Assert.Equal(ImportStatus.AwaitingApproval, after.Status);
        Assert.True(File.Exists(before.FilePath));
        Assert.Null(after.AcceptedBook);
    }

    [Fact]
    public async Task Cross_origin_is_rejected_even_when_fetch_metadata_says_same_site()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var id = await app.SeedAsync();
        var before = await app.ReadAsync(id);

        using var response = await AcceptAsync(client, app, id, request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", "http://attacker.example");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(id)).Status);
        Assert.True(File.Exists(before.FilePath));
    }

    [Fact]
    public async Task Matching_origin_authority_including_port_is_allowed_and_different_port_is_denied()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();

        var defaultAuthorityId = await app.SeedAsync();
        using (var response = await AcceptAsync(client, app, defaultAuthorityId, request =>
            request.Headers.TryAddWithoutValidation("Origin", "http://localhost")))
        {
            Assert.True(response.IsSuccessStatusCode);
        }
        Assert.Equal(ImportStatus.Completed, (await app.ReadAsync(defaultAuthorityId)).Status);

        var allowedId = await app.SeedAsync();
        using (var response = await AcceptAsync(client, app, allowedId, request =>
        {
            request.Headers.Host = "localhost:5187";
            request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5187");
        }))
        {
            Assert.True(response.IsSuccessStatusCode);
        }
        var allowed = await app.ReadAsync(allowedId);
        Assert.Equal(ImportStatus.Completed, allowed.Status);
        Assert.False(File.Exists(allowed.FilePath));
        Assert.NotNull(allowed.AcceptedBook);

        var deniedId = await app.SeedAsync();
        var deniedBefore = await app.ReadAsync(deniedId);
        using (var response = await AcceptAsync(client, app, deniedId, request =>
        {
            request.Headers.Host = "localhost:5187";
            request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5188");
        }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(deniedId)).Status);
        Assert.True(File.Exists(deniedBefore.FilePath));
    }

    [Fact]
    public async Task Headerless_nonbrowser_request_is_allowed_but_an_unrecognized_host_is_rejected()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();

        var id = await app.SeedAsync();
        using (var response = await AcceptAsync(client, app, id))
        {
            Assert.True(response.IsSuccessStatusCode);
        }
        Assert.Equal(ImportStatus.Completed, (await app.ReadAsync(id)).Status);

        var hostileHostId = await app.SeedAsync();
        var before = await app.ReadAsync(hostileHostId);
        using (var response = await AcceptAsync(client, app, hostileHostId, request =>
        {
            request.Headers.Host = "attacker.example";
            request.Headers.TryAddWithoutValidation("Origin", "http://attacker.example");
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(ImportStatus.AwaitingApproval, (await app.ReadAsync(hostileHostId)).Status);
        Assert.True(File.Exists(before.FilePath));
    }

    private static async Task<HttpResponseMessage> AcceptAsync(
        HttpClient client,
        PaparrFactory app,
        long importId,
        Action<HttpRequestMessage>? configure = null)
    {
        var job = await app.ReadAsync(importId);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/imports/{importId}/accept/{job.Candidates.Single().Id}");
        configure?.Invoke(request);
        return await client.SendAsync(request);
    }
}
