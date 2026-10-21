using System.Text.Json;
using Microsoft.Extensions.Options;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class MetadataEnricherService(HttpClient client, IOptions<IngestionOptions> options,
    TimeProvider clock, ILogger<MetadataEnricherService> logger, MetadataProviderGateway gateway) : IMetadataEnricherService
{
    public Task<List<MetadataCandidate>> QueryOpenLibraryAsync(string title, string author, long importJobId, CancellationToken cancellationToken = default) =>
        QueryAsync($"https://openlibrary.org/search.json?title={Uri.EscapeDataString(title)}&author={Uri.EscapeDataString(author)}&limit=5",
            "openlibrary", title, author, importJobId, cancellationToken);

    public Task<List<MetadataCandidate>> QueryGoogleBooksAsync(string title, string author, long importJobId, CancellationToken cancellationToken = default) =>
        QueryAsync($"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString($"intitle:{title} inauthor:{author}")}&maxResults=5",
            "googlebooks", title, author, importJobId, cancellationToken);

    private async Task<List<MetadataCandidate>> QueryAsync(string url, string source, string title, string author, long jobId, CancellationToken ct)
    {
        if (!options.Value.MetadataProvidersEnabled) return [];
        var candidates = new List<MetadataCandidate>();
        try
        {
            var body = await gateway.GetAsync(client, url, source, ct);
            if (body is null) return candidates;
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty(source == "openlibrary" ? "docs" : "items", out var items) ||
                items.ValueKind != JsonValueKind.Array) return candidates;
            foreach (var item in items.EnumerateArray().Take(5))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var data = item;
                if (source == "googlebooks" && !item.TryGetProperty("volumeInfo", out data)) continue;
                if (data.ValueKind != JsonValueKind.Object) continue;
                var candidateTitle = Text(data, "title");
                if (!data.TryGetProperty(source == "openlibrary" ? "author_name" : "authors", out var authors) ||
                    authors.ValueKind != JsonValueKind.Array) continue;
                var candidateAuthor = string.Join(", ", authors.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()).Where(a => !string.IsNullOrWhiteSpace(a)));
                var id = Text(item, source == "openlibrary" ? "key" : "id");
                if (string.IsNullOrWhiteSpace(candidateTitle) || string.IsNullOrWhiteSpace(candidateAuthor) || string.IsNullOrWhiteSpace(id)) continue;
                var match = MetadataMatcher.Compare(title, author, candidateTitle, candidateAuthor);
                candidates.Add(new MetadataCandidate
                {
                    Title = candidateTitle,
                    Author = candidateAuthor,
                    ExternalId = id,
                    Source = source,
                    ConfidenceScore = match.Score,
                    Evidence = match.Evidence,
                    ImportJobId = jobId,
                    CreatedAt = clock.GetUtcNow().UtcDateTime
                });
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Metadata provider {Provider} timed out", source);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Metadata provider {Provider} could not be read", source);
        }
        return candidates;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
