using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;

namespace Paparr.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class EditionMetadataTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Selected_candidate_edition_is_persisted_and_exposed_in_review_and_library_responses()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var jobId = await app.SeedAsync();
        long candidateId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(item => item.Candidates).SingleAsync(item => item.Id == jobId);
            var candidate = job.Candidates.Single();
            candidate.Isbn13 = "9780306406157";
            candidate.IsbnStatus = EditionIsbnStatus.Valid;
            candidate.Publisher = "Press";
            candidate.PublicationYear = 2020;
            candidate.Language = "en";
            candidateId = candidate.Id;
            await db.SaveChangesAsync();
        }

        using var response = await client.PostAsync($"/api/imports/{jobId}/accept/{candidateId}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await response.Content.ReadFromJsonAsync<ImportJobDto>();
        Assert.NotNull(details);
        Assert.Equal("9780306406157", Assert.Single(details.Candidates).Edition!.Isbn13);
        Assert.Equal("Valid", details.AcceptedBook!.Edition!.IsbnStatus);
        Assert.Equal("Press", details.AcceptedBook.Edition.Publisher);
        Assert.Equal(2020, details.AcceptedBook.Edition.PublicationYear);
        Assert.Equal("en", details.AcceptedBook.Edition.Language);

        var library = await client.GetFromJsonAsync<LibraryPageDto>("/api/library");
        Assert.NotNull(library);
        var book = Assert.Single(library.Items);
        Assert.Equal("9780306406157", book.Edition!.Isbn13);
        Assert.Equal("Press", book.Edition.Publisher);
    }

    [Fact]
    public async Task Manual_title_author_choice_does_not_inherit_another_candidates_edition()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var jobId = await app.SeedAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidate = await db.MetadataCandidates.SingleAsync(item => item.ImportJobId == jobId);
            candidate.Isbn13 = "9780306406157";
            candidate.IsbnStatus = EditionIsbnStatus.Valid;
            candidate.Publisher = "Original Press";
            await db.SaveChangesAsync();
        }

        using var invalidBounds = await client.PostAsJsonAsync($"/api/imports/{jobId}/metadata",
            new { title = "Bad year", author = "Manual author", edition = new { publicationYear = 999 } });
        Assert.Equal(HttpStatusCode.BadRequest, invalidBounds.StatusCode);

        using var edit = await client.PostAsJsonAsync($"/api/imports/{jobId}/metadata",
            new { title = "Manual title", author = "Manual author" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = await edit.Content.ReadFromJsonAsync<ImportJobDto>();
        Assert.NotNull(edited);
        var manual = Assert.Single(edited.Candidates, candidate => candidate.Source == "manual");
        Assert.Equal("NotProvided", manual.Edition!.IsbnStatus);
        Assert.Null(manual.Edition.Isbn13);
        Assert.Null(manual.Edition.Publisher);

        using var manualEditionResponse = await client.PostAsJsonAsync($"/api/imports/{jobId}/metadata",
            new
            {
                title = "Manual edition",
                author = "Manual author",
                edition = new { isbn = "9780306406158", publisher = "Manual Press", publicationYear = 2022, language = "fr" }
            });
        Assert.Equal(HttpStatusCode.OK, manualEditionResponse.StatusCode);
        var withEdition = await manualEditionResponse.Content.ReadFromJsonAsync<ImportJobDto>();
        Assert.NotNull(withEdition);
        var entered = Assert.Single(withEdition.Candidates, candidate => candidate.Title == "Manual edition");
        Assert.Equal("Invalid", entered.Edition!.IsbnStatus);
        Assert.Null(entered.Edition.Isbn13);
        Assert.Equal("Manual Press", entered.Edition.Publisher);
        Assert.Equal(2022, entered.Edition.PublicationYear);
        Assert.Equal("fr", entered.Edition.Language);

        using var accepted = await client.PostAsync($"/api/imports/{jobId}/accept/{manual.Id}", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var completed = await accepted.Content.ReadFromJsonAsync<ImportJobDto>();
        Assert.NotNull(completed);
        Assert.Equal("NotProvided", completed.AcceptedBook!.Edition!.IsbnStatus);
        Assert.Null(completed.AcceptedBook.Edition.Publisher);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("author")]
    [InlineData("publisher")]
    public async Task Manual_text_with_null_character_is_rejected_without_persisting_a_candidate(string field)
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var jobId = await app.SeedAsync();
        var before = await app.ReadAsync(jobId);
        object request = field switch
        {
            "title" => new { title = "Manual\0 title", author = "Manual author" },
            "author" => new { title = "Manual title", author = "Manual\0 author" },
            _ => new { title = "Manual title", author = "Manual author", edition = new { publisher = "Manual\0 press" } }
        };

        using var response = await client.PostAsJsonAsync($"/api/imports/{jobId}/metadata", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = await app.ReadAsync(jobId);
        Assert.Equal(ImportStatus.AwaitingApproval, after.Status);
        Assert.Equal(before.Candidates.Count, after.Candidates.Count);
        Assert.Equal(before.Revision, after.Revision);
        using var valid = await client.PostAsJsonAsync($"/api/imports/{jobId}/metadata",
            new { title = "Valid manual choice", author = "Manual author" });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(before.Candidates.Count + 1, (await app.ReadAsync(jobId)).Candidates.Count);
    }

    [Fact]
    public async Task Upgrade_from_previous_schema_keeps_candidate_and_book_rows_with_not_provided_editions()
    {
        using var app = await PaparrFactory.CreateAsync(postgres);
        using var client = app.CreateClient();
        var jobId = await app.SeedAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ImportJobs.Include(item => item.Candidates).SingleAsync(item => item.Id == jobId);
            db.Books.Add(new Book
            {
                Title = "Existing book",
                Author = "Existing author",
                Source = "old catalog",
                ExternalId = "existing-id",
                FilePath = "existing/file.pdf",
                ImportedAt = DateTime.UtcNow,
                ImportJobId = jobId
            });
            await db.SaveChangesAsync();

            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261021152318_DuplicateSources");
            await migrator.MigrateAsync("20261021224910_EditionMetadata");
        }

        using var verificationScope = app.Services.CreateScope();
        var upgradedDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var candidate = await upgradedDb.MetadataCandidates.AsNoTracking().SingleAsync(item => item.ImportJobId == jobId);
        var book = await upgradedDb.Books.AsNoTracking().SingleAsync(item => item.ImportJobId == jobId);
        Assert.Equal("The Time Machine", candidate.Title);
        Assert.Equal("Existing book", book.Title);
        Assert.Equal(EditionIsbnStatus.NotProvided, candidate.IsbnStatus);
        Assert.Equal(EditionIsbnStatus.NotProvided, book.IsbnStatus);
    }
}
