using Paparr.API.Domain;

namespace Paparr.API.Services;

public interface IMetadataEnricherService
{
    Task<List<MetadataCandidate>> QueryOpenLibraryAsync(string title, string author, long importJobId, CancellationToken cancellationToken = default);
    Task<List<MetadataCandidate>> QueryGoogleBooksAsync(string title, string author, long importJobId, CancellationToken cancellationToken = default);
}
