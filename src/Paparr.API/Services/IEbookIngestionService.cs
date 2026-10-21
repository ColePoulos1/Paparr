using Paparr.API.Domain;

namespace Paparr.API.Services;

public interface IEbookIngestionService
{
    Task ProcessImportJobAsync(ImportJob job, CancellationToken cancellationToken = default);
    Task AcceptCandidateAsync(ImportJob job, MetadataCandidate candidate, CancellationToken cancellationToken = default);
    Task RecoverAsync(ImportJob job, CancellationToken cancellationToken = default);
}
