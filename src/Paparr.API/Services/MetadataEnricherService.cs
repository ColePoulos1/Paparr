using System.Text.Json;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public class MetadataEnricherService : IMetadataEnricherService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MetadataEnricherService> _logger;

    public MetadataEnricherService(HttpClient httpClient, ILogger<MetadataEnricherService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<MetadataCandidate>> QueryOpenLibraryAsync(string title, string author, long importJobId)
    {
        var candidates = new List<MetadataCandidate>();

        try
        {
            var query = System.Web.HttpUtility.UrlEncode($"{title} {author}");
            var url = $"https://openlibrary.org/search.json?title={query}&limit=5";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Open Library API returned {StatusCode} for query: {Title} {Author}", 
                    response.StatusCode, title, author);
                return candidates;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var docs = doc.RootElement.GetProperty("docs");

            foreach (var doc_elem in docs.EnumerateArray().Take(5))
            {
                if (doc_elem.TryGetProperty("title", out var titleElem) &&
                    doc_elem.TryGetProperty("author_name", out var authorsElem) &&
                    doc_elem.TryGetProperty("key", out var keyElem))
                {
                    var candidateTitle = titleElem.GetString() ?? "Unknown";
                    var candidateAuthor = authorsElem.EnumerateArray().FirstOrDefault().GetString() ?? "Unknown";
                    var externalId = keyElem.GetString()?.Replace("/works/", "") ?? "";

                    // Simple confidence scoring
                    var confidence = CalculateConfidence(title, candidateTitle, author, candidateAuthor);

                    candidates.Add(new MetadataCandidate
                    {
                        Title = candidateTitle,
                        Author = candidateAuthor,
                        Source = "openlibrary",
                        ExternalId = externalId,
                        ConfidenceScore = confidence,
                        ImportJobId = importJobId,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Open Library for {Title} {Author}", title, author);
        }

        return candidates;
    }

    public async Task<List<MetadataCandidate>> QueryGoogleBooksAsync(string title, string author, long importJobId)
    {
        var candidates = new List<MetadataCandidate>();

        try
        {
            var query = System.Web.HttpUtility.UrlEncode($"{title} {author}");
            var url = $"https://www.googleapis.com/books/v1/volumes?q={query}&maxResults=5";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Books API returned {StatusCode} for query: {Title} {Author}", 
                    response.StatusCode, title, author);
                return candidates;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            
            if (!doc.RootElement.TryGetProperty("items", out var items))
            {
                return candidates;
            }

            foreach (var item in items.EnumerateArray().Take(5))
            {
                if (item.TryGetProperty("volumeInfo", out var volumeInfo))
                {
                    if (volumeInfo.TryGetProperty("title", out var titleElem) &&
                        volumeInfo.TryGetProperty("authors", out var authorsElem) &&
                        item.TryGetProperty("id", out var idElem))
                    {
                        var candidateTitle = titleElem.GetString() ?? "Unknown";
                        var candidateAuthor = authorsElem.EnumerateArray().FirstOrDefault().GetString() ?? "Unknown";
                        var externalId = idElem.GetString() ?? "";

                        var confidence = CalculateConfidence(title, candidateTitle, author, candidateAuthor);

                        candidates.Add(new MetadataCandidate
                        {
                            Title = candidateTitle,
                            Author = candidateAuthor,
                            Source = "googlebooks",
                            ExternalId = externalId,
                            ConfidenceScore = confidence,
                            ImportJobId = importJobId,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Google Books for {Title} {Author}", title, author);
        }

        return candidates;
    }

    private decimal CalculateConfidence(string originalTitle, string candidateTitle, string originalAuthor, string candidateAuthor)
    {
        var titleSimilarity = CalculateSimilarity(originalTitle.ToLower(), candidateTitle.ToLower());
        var authorSimilarity = CalculateSimilarity(originalAuthor.ToLower(), candidateAuthor.ToLower());

        // Weight: 60% title, 40% author
        return (titleSimilarity * 0.6m + authorSimilarity * 0.4m) * 100;
    }

    private decimal CalculateSimilarity(string a, string b)
    {
        var longer = a.Length > b.Length ? a : b;
        var shorter = a.Length > b.Length ? b : a;

        if (longer.Length == 0)
            return 1.0m;

        var editDistance = ComputeLevenshteinDistance(longer, shorter);
        return (longer.Length - editDistance) / (decimal)longer.Length;
    }

    private int ComputeLevenshteinDistance(string a, string b)
    {
        var distances = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++)
            distances[i, 0] = i;

        for (int j = 0; j <= b.Length; j++)
            distances[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[a.Length, b.Length];
    }
}
