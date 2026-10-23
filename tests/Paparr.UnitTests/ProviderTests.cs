using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class ProviderTests
{
    [Theory]
    [InlineData("""{"docs":[{"title":"Book","author_name":[],"key":"/works/1"},{"title":"Book","author_name":["Author"],"key":"/works/2"}]}""", 1)]
    [InlineData("""{"docs":[{"title":"Book","author_name":null,"key":"/works/1"}]}""", 0)]
    [InlineData("""{"docs":null}""", 0)]
    [InlineData("""{"other":[]}""", 0)]
    [InlineData("""not json""", 0)]
    public async Task OpenLibrary_handles_missing_and_malformed_fields_per_candidate(string json, int expected)
    {
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var service = Create(client);
        Assert.Equal(expected, (await service.QueryOpenLibraryAsync("Book", "Author", 7)).Count);
    }

    [Fact]
    public async Task OpenLibrary_skips_text_with_null_characters_and_keeps_following_items()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"docs":[{"title":"Bad\u0000 title","author_name":["Author"],"key":"/works/bad-title"},{"title":"Book","author_name":["Bad\u0000 author"],"key":"/works/bad-author"},{"title":"Book","author_name":["Author"],"key":"/works/bad\u0000-id"},{"title":"Good title","author_name":["Author"],"key":"/works/good","publisher":["Bad\u0000 press"]},{"title":"Another good title","author_name":["Author"],"key":"/works/another"}]}""")
        }));

        var candidates = await Create(client).QueryOpenLibraryAsync("Good title", "Author", 7);

        Assert.Equal(2, candidates.Count);
        var candidate = candidates[0];
        Assert.Equal("Good title", candidate.Title);
        Assert.Equal("/works/good", candidate.ExternalId);
        Assert.Null(candidate.Publisher);
        Assert.Equal("/works/another", candidates[1].ExternalId);
    }

    [Fact]
    public async Task OpenLibrary_keeps_title_and_author_in_separate_query_parameters()
    {
        Uri? uri = null;
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            uri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"docs":[]}""") };
        }));
        await Create(client).QueryOpenLibraryAsync("A & B", "Some Author", 7);
        Assert.NotNull(uri);
        Assert.Contains("title=A%20%26%20B", uri.Query);
        Assert.Contains("author=Some%20Author", uri.Query);
        Assert.Contains("fields=key,title,author_name,isbn,publisher,publish_year,language", uri.Query);
    }

    [Fact]
    public async Task OpenLibrary_parses_unambiguous_edition_metadata_and_normalizes_isbn()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"docs":[{"title":"Book","author_name":["Author"],"key":"/works/1","isbn":["0-306-40615-2"],"publisher":["Press"],"publish_year":[2020],"language":["/languages/eng"]}]}""")
        }));

        var candidate = Assert.Single(await Create(client).QueryOpenLibraryAsync("Book", "Author", 9));

        Assert.Equal("9780306406157", candidate.Isbn13);
        Assert.Equal(Paparr.API.Domain.EditionIsbnStatus.Valid, candidate.IsbnStatus);
        Assert.Equal("Press", candidate.Publisher);
        Assert.Equal(2020, candidate.PublicationYear);
        Assert.Equal("eng", candidate.Language);
        Assert.Contains("work-level summaries and may span editions", candidate.Evidence);
    }

    [Fact]
    public async Task OpenLibrary_does_not_choose_one_isbn_from_multiple_work_editions()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"docs":[{"title":"Book","author_name":["Author"],"key":"/works/1","isbn":["9780306406157","9780140328721"],"publisher":["Press"],"publish_year":[2020,2022],"language":["en"]}]}""")
        }));

        var candidate = Assert.Single(await Create(client).QueryOpenLibraryAsync("Book", "Author", 9));

        Assert.Equal(Paparr.API.Domain.EditionIsbnStatus.Ambiguous, candidate.IsbnStatus);
        Assert.Null(candidate.Isbn13);
        Assert.Null(candidate.PublicationYear);
        Assert.Equal("Press", candidate.Publisher);
        Assert.Equal("en", candidate.Language);
    }

    [Fact]
    public async Task Google_books_parses_invalid_and_ambiguous_edition_identifiers_without_guessing()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"items":[{"id":"v1","volumeInfo":{"title":"Book","authors":["Author"],"industryIdentifiers":[{"type":"ISBN_13","identifier":"9780306406158"},{"type":"ISBN_13","identifier":"9780306406157"},{"type":"ISBN_13","identifier":"9780140328721"}],"publisher":"Press","publishedDate":"2021-05-02","language":"fr"}}]}""")
        }));

        var candidate = Assert.Single(await Create(client).QueryGoogleBooksAsync("Book", "Author", 9));

        Assert.Null(candidate.Isbn13);
        Assert.Equal(Paparr.API.Domain.EditionIsbnStatus.Ambiguous, candidate.IsbnStatus);
        Assert.Equal("Press", candidate.Publisher);
        Assert.Equal(2021, candidate.PublicationYear);
        Assert.Equal("fr", candidate.Language);
    }

    [Fact]
    public async Task Throttling_keeps_local_review_available()
    {
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.Empty(await Create(client).QueryGoogleBooksAsync("Book", "Author", 1));
    }

    [Fact]
    public async Task Cancellation_is_propagated_to_the_caller()
    {
        using var client = new HttpClient(new ResponseHandler(_ => throw new OperationCanceledException()));
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).QueryGoogleBooksAsync("Book", "Author", 1, ct.Token));
    }

    private static MetadataEnricherService Create(HttpClient client) => new(client, Options.Create(new IngestionOptions()),
        TimeProvider.System, NullLogger<MetadataEnricherService>.Instance,
        new MetadataProviderGateway(TimeProvider.System, NullLogger<MetadataProviderGateway>.Instance));

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
