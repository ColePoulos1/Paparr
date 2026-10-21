using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class MetadataEdgeCaseTests
{
    [Fact]
    public void Matching_is_stable_under_turkish_current_culture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");

            var match = MetadataMatcher.Compare("INDIGO", "I. Author", "indigo", "i. author");

            Assert.Equal(100, match.Score);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public async Task GoogleBooks_joins_all_string_authors_and_skips_malformed_entries()
    {
        const string json = """{"items":[null,{"volumeInfo":null},{"volumeInfo":{"title":"A Book","authors":["First Author",null,17,"Second Author"]},"id":"book-1"}]}""";
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

        var candidates = await CreateService(client).QueryGoogleBooksAsync("A Book", "First Author, Second Author", 9);

        var candidate = Assert.Single(candidates);
        Assert.Equal("First Author, Second Author", candidate.Author);
        Assert.Equal("book-1", candidate.ExternalId);
        Assert.Equal(100, candidate.ConfidenceScore);
        Assert.Equal(9, candidate.ImportJobId);
    }

    [Fact]
    public async Task Provider_timeout_without_caller_cancellation_returns_no_candidates()
    {
        using var client = CreateClient(_ => throw new OperationCanceledException("provider timeout"));

        var candidates = await CreateService(client).QueryOpenLibraryAsync("Book", "Author", 3);

        Assert.Empty(candidates);
    }

    private static MetadataEnricherService CreateService(HttpClient client) => new(
        client,
        Options.Create(new IngestionOptions()),
        TimeProvider.System,
        NullLogger<MetadataEnricherService>.Instance,
        new MetadataProviderGateway(TimeProvider.System, NullLogger<MetadataProviderGateway>.Instance));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new ResponseHandler(respond));

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
