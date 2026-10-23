using System.Text.Json;
using Microsoft.Extensions.Options;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class MetadataEnricherService(HttpClient client, IOptions<IngestionOptions> options,
    TimeProvider clock, ILogger<MetadataEnricherService> logger, MetadataProviderGateway gateway) : IMetadataEnricherService
{
    public Task<List<MetadataCandidate>> QueryOpenLibraryAsync(string title, string author, long importJobId, CancellationToken cancellationToken = default) =>
        QueryAsync($"https://openlibrary.org/search.json?title={Uri.EscapeDataString(title)}&author={Uri.EscapeDataString(author)}&limit=5&fields=key,title,author_name,isbn,publisher,publish_year,language",
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
                if (!UsableText(candidateTitle) || !UsableText(candidateAuthor) || !UsableText(id)) continue;
                var match = MetadataMatcher.Compare(title, author, candidateTitle, candidateAuthor);
                var edition = ReadEdition(data, source);
                candidates.Add(new MetadataCandidate
                {
                    Title = candidateTitle,
                    Author = candidateAuthor,
                    ExternalId = id,
                    Source = source,
                    ConfidenceScore = match.Score,
                    Evidence = source == "openlibrary"
                        ? $"{match.Evidence} Open Library edition fields are work-level summaries and may span editions."
                        : match.Evidence,
                    Isbn13 = edition.Isbn13,
                    IsbnStatus = edition.IsbnStatus,
                    Publisher = edition.Publisher,
                    PublicationYear = edition.PublicationYear,
                    Language = edition.Language,
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

    private static bool UsableText([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');

    private static NormalizedEditionMetadata ReadEdition(JsonElement data, string source)
    {
        var isbns = source == "openlibrary"
            ? ReadStringArray(data, "isbn")
            : ReadGoogleIsbns(data);
        var publisher = source == "openlibrary"
            ? Unique(ReadStringArray(data, "publisher"))
            : Text(data, "publisher");
        var publicationDate = source == "openlibrary"
            ? Unique(ReadStringArray(data, "publish_year", allowNumbers: true))
            : Text(data, "publishedDate");
        var language = source == "openlibrary"
            ? TrimLanguagePath(Unique(ReadStringArray(data, "language")))
            : Text(data, "language");
        return EditionMetadataNormalizer.Create(isbns, publisher, publicationDate, language);
    }

    private static List<string?> ReadGoogleIsbns(JsonElement data)
    {
        if (!data.TryGetProperty("industryIdentifiers", out var identifiers) || identifiers.ValueKind != JsonValueKind.Array)
            return [];
        return identifiers.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() is "ISBN_10" or "ISBN_13")
            .Select(item => item.TryGetProperty("identifier", out var identifier) && identifier.ValueKind == JsonValueKind.String
                ? identifier.GetString() : null).ToList();
    }

    private static List<string?> ReadStringArray(JsonElement data, string property, bool allowNumbers = false)
    {
        if (!data.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()
            : allowNumbers && value.ValueKind == JsonValueKind.Number ? value.ToString() : null).ToList();
    }

    private static string? Unique(IEnumerable<string?> values)
    {
        var distinct = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static string? TrimLanguagePath(string? value) =>
        value is not null && value.StartsWith("/languages/", StringComparison.Ordinal)
            ? value["/languages/".Length..] : value;
}
