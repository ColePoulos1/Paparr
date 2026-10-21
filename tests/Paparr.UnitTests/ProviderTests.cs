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
