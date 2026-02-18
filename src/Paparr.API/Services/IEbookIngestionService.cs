using Paparr.API.Domain;

namespace Paparr.API.Services;

public interface IEbookIngestionService
{
    /// <summary>
    /// Processes a pending import job:
    /// 1. Extracts metadata from file
    /// 2. Queries external APIs
    /// 3. Stores candidates
    /// 4. Transitions job to AwaitingApproval if high confidence match found
    /// </summary>
    Task ProcessImportJobAsync(ImportJob job);

    /// <summary>
    /// Accepts a metadata candidate for an import job.
    /// Creates a Book record and moves file to library.
    /// </summary>
    Task AcceptCandidateAsync(ImportJob job, MetadataCandidate candidate);
}
