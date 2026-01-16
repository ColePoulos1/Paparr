using Paparr.API.Domain;

namespace Paparr.API.Services;

public interface IMetadataEnricherService
{
    /// <summary>
    /// Queries Open Library API for metadata candidates.
    /// </summary>
    Task<List<MetadataCandidate>> QueryOpenLibraryAsync(string title, string author, long importJobId);

    /// <summary>
    /// Queries Google Books API for metadata candidates.
    /// </summary>
    Task<List<MetadataCandidate>> QueryGoogleBooksAsync(string title, string author, long importJobId);
}
