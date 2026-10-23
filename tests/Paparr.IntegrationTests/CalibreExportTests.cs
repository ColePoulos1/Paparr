using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Authentication;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class CalibreExportTests(PostgresFixture postgres)
{
    private const string Password = "calibre export test password";
    private const string OpfNamespace = "http://www.idpf.org/2007/opf";
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";

    [Theory]
    [InlineData(".epub", "book.epub")]
    [InlineData(".pdf", "book.pdf")]
    public async Task Export_is_owner_only_contains_verified_book_and_metadata_and_does_not_mutate_library(
        string extension, string bookEntryName)
    {
        using var app = await CreateAuthenticatedFactoryAsync();
        using var client = app.CreateClient();
        var seeded = await SeedBookAsync(app, extension, [4, 9, 16, 25], title: "A / Book", author: "Reader");
        var originalBytes = await File.ReadAllBytesAsync(seeded.Path);
        var originalHash = Convert.ToHexString(SHA256.HashData(originalBytes));

        using (var anonymous = await client.GetAsync($"/api/library/{seeded.Id}/export"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await SignInAsync(client);

        using var response = await client.GetAsync($"/api/library/{seeded.Id}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Equal($"paparr-book-{seeded.Id}.zip", fileName);

        await using var zipStream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.Equal(new[] { "README.txt", bookEntryName, "metadata.opf" },
            archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        await using var exportedBook = archive.GetEntry(bookEntryName)!.Open();
        using var exportedBytes = new MemoryStream();
        await exportedBook.CopyToAsync(exportedBytes);
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(exportedBytes.ToArray())));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(seeded.Path));

        using (var opf = archive.GetEntry("metadata.opf")!.Open())
        {
            var xml = await XDocument.LoadAsync(opf, LoadOptions.None, default);
            XNamespace opfNs = OpfNamespace;
            XNamespace dcNs = DcNamespace;
            Assert.Equal("A / Book", (string?)xml.Root?.Element(opfNs + "metadata")?.Element(dcNs + "title"));
            Assert.Equal("Reader", (string?)xml.Root?.Element(opfNs + "metadata")?.Element(dcNs + "creator"));
            Assert.Equal("Test publisher", (string?)xml.Root?.Element(opfNs + "metadata")?.Element(dcNs + "publisher"));
        }

        var readme = await ReadEntryAsync(archive.GetEntry("README.txt")!);
        Assert.Contains($"ebook-meta {bookEntryName} --from-opf metadata.opf", readme, StringComparison.Ordinal);
        Assert.Contains($"calibredb add {bookEntryName} --with-library PATH", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(app.Root, readme, StringComparison.OrdinalIgnoreCase);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(ImportStatus.Completed,
            (await db.ImportJobs.AsNoTracking().SingleAsync(job => job.AcceptedBook!.Id == seeded.Id)).Status);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(seeded.Path));
    }

    [Fact]
    public async Task Unknown_book_is_404_and_changed_file_is_409_without_rewriting_it()
    {
        using var app = await CreateAuthenticatedFactoryAsync();
        using var client = app.CreateClient();
        await SignInAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/library/999999/export")).StatusCode);

        var seeded = await SeedBookAsync(app, ".pdf", [1, 2, 3]);
        var changedBytes = new byte[] { 8, 8, 8, 8 };
        await File.WriteAllBytesAsync(seeded.Path, changedBytes);
        using var conflict = await client.GetAsync($"/api/library/{seeded.Id}/export");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadAsStringAsync();
        Assert.DoesNotContain(app.Root, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(changedBytes, await File.ReadAllBytesAsync(seeded.Path));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(ImportStatus.Completed,
            (await db.ImportJobs.AsNoTracking().SingleAsync(job => job.AcceptedBook!.Id == seeded.Id)).Status);
    }

    private async Task<PaparrFactory> CreateAuthenticatedFactoryAsync()
    {
        var app = await PaparrFactory.CreateAsync(postgres);
        app.SettingsOverrides["Authentication:Enabled"] = "true";
        app.SettingsOverrides["Authentication:PasswordHash"] = OwnerPassword.Hash(Password);
        app.SettingsOverrides["Authentication:KeysPath"] = Path.Combine(app.Root, "keys");
        return app;
    }

    private static async Task<(long Id, string Path)> SeedBookAsync(PaparrFactory app, string extension, byte[] bytes,
        string title = "Test title", string author = "Test author")
    {
        var path = Path.Combine(app.Library, "calibre-fixtures", Guid.NewGuid().ToString("N") + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var now = DateTime.UtcNow;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = new ImportJob
        {
            FilePath = Path.Combine(app.Ingest, Guid.NewGuid().ToString("N") + extension),
            FileHash = Convert.ToHexString(SHA256.HashData(bytes)),
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
            Isbn13 = "9780306406157",
            IsbnStatus = EditionIsbnStatus.Valid,
            Publisher = "Test\u0001 publisher",
            PublicationYear = 2001,
            Language = "en",
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
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed record SessionDto(bool Enabled, bool Authenticated, string CsrfToken);
}
