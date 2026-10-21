using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Authentication;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class LibraryDownloadTests(PostgresFixture postgres)
{
    private const string Password = "verified library download password";

    [Theory]
    [InlineData(".pdf", "application/pdf", "The _ Safe_ Títle - Å_ Reader.pdf")]
    [InlineData(".epub", "application/epub+zip", "The _ Safe_ Títle - Å_ Reader.epub")]
    public async Task Download_requires_owner_and_returns_verified_attachment_with_ranges(
        string extension, string contentType, string expectedFileName)
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["Authentication:Enabled"] = "true";
        app.SettingsOverrides["Authentication:PasswordHash"] = OwnerPassword.Hash(Password);
        app.SettingsOverrides["Authentication:KeysPath"] = Path.Combine(app.Root, "keys");
        using var client = app.CreateClient();
        var book = await SeedBookAsync(app, "The / Safe? Títle", "Å: Reader", extension, [1, 2, 3, 4, 5]);

        using var anonymous = await client.GetAsync($"/api/library/{book.Id}/file");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await SignInAsync(client);

        using var full = await client.GetAsync($"/api/library/{book.Id}/file");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.True(full.Headers.CacheControl?.Private);
        Assert.True(full.Headers.CacheControl?.NoStore);
        Assert.Equal(contentType, full.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedFileName, full.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await full.Content.ReadAsByteArrayAsync());

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/library/{book.Id}/file");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);
        using var partial = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4 }, await partial.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 1-3/5", partial.Content.Headers.ContentRange?.ToString());

        using var invalidRangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/library/{book.Id}/file");
        invalidRangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(20, 25);
        using var invalidRange = await client.SendAsync(invalidRangeRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalidRange.StatusCode);
        Assert.Equal(5, invalidRange.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task Unknown_book_is_404_and_known_unsafe_or_unavailable_files_are_conflicts_without_paths()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["MaxFileSizeMb"] = "1";
        using var client = app.CreateClient();
        using var unknown = await client.GetAsync("/api/library/999999/file");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var missing = await SeedBookAsync(app, "Missing", "Author", ".pdf", [1, 2, 3]);
        File.Delete(missing.Path);
        var changed = await SeedBookAsync(app, "Changed", "Author", ".pdf", [4, 5, 6]);
        await File.WriteAllBytesAsync(changed.Path, [7, 8, 9]);
        var outsidePath = Path.Combine(app.Root, $"outside-{Guid.NewGuid():N}.pdf");
        var outside = await SeedBookAsync(app, "Outside", "Author", ".pdf", [10, 11], outsidePath);
        var directory = await SeedBookAsync(app, "Directory", "Author", ".pdf", [12, 13]);
        File.Delete(directory.Path);
        Directory.CreateDirectory(directory.Path);
        var unsupported = await SeedBookAsync(app, "Unsupported", "Author", ".txt", [14, 15]);
        var oversize = await SeedBookAsync(app, "Oversize", "Author", ".pdf", [16, 17]);
        await File.WriteAllBytesAsync(oversize.Path, new byte[1024 * 1024 + 1]);

        foreach (var book in new[] { missing, changed, outside, directory, unsupported, oversize })
        {
            using var response = await client.GetAsync($"/api/library/{book.Id}/file");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(app.Root, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(outsidePath, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(changed.Path));
        Assert.Equal(new byte[] { 10, 11 }, await File.ReadAllBytesAsync(outside.Path));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(ImportStatus.Completed, (await db.ImportJobs.AsNoTracking().SingleAsync(j => j.AcceptedBook!.Id == changed.Id)).Status);
        Assert.Equal(ImportStatus.Completed, (await db.ImportJobs.AsNoTracking().SingleAsync(j => j.AcceptedBook!.Id == outside.Id)).Status);
    }

    private static async Task<(long Id, string Path)> SeedBookAsync(PaparrFactory app, string title, string author,
        string extension, byte[] bytes, string? path = null)
    {
        path ??= Path.Combine(app.Library, "download-fixtures", Guid.NewGuid().ToString("N") + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var now = DateTime.UtcNow;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = new ImportJob
        {
            FilePath = Path.Combine(app.Ingest, Guid.NewGuid().ToString("N") + extension),
            FileHash = hash,
            Status = ImportStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now
        };
        var book = new Book
        {
            Title = title,
            Author = author,
            Source = "integration-test",
            ExternalId = "",
            FilePath = path,
            ImportedAt = now,
            ImportJob = job
        };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return (book.Id, path);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<SessionDto>("/api/auth/session");
        Assert.NotNull(session);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { password = Password })
        };
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", session.CsrfToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Paparr.Session="));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie.Split(';', 2)[0]);
    }

    private sealed record SessionDto(bool Enabled, bool Authenticated, string CsrfToken);
}
