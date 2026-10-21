using System.ComponentModel.DataAnnotations;
using Paparr.API.Domain;

namespace Paparr.API.Models;

public sealed record ImportJobDto(long Id, string FileName, string Status, DateTime CreatedAt, DateTime UpdatedAt,
    Guid Revision, int AttemptCount, string? LastError, IReadOnlyList<MetadataCandidateDto> Candidates, BookDto? AcceptedBook,
    IReadOnlyList<ImportEventDto> Events, SourceCleanupDto Cleanup)
{
    public static ImportJobDto From(ImportJob job, bool cleanupEnabled = false) => new(job.Id, Path.GetFileName(job.FilePath), job.Status.ToString(),
        job.CreatedAt, job.UpdatedAt, job.Revision, job.AttemptCount, job.LastError,
        job.Candidates.OrderByDescending(c => c.ConfidenceScore).ThenBy(c => c.Id)
            .Select(c => new MetadataCandidateDto(c.Id, c.Title, c.Author, c.Source, c.ExternalId, c.ConfidenceScore, c.Evidence)).ToList(),
        job.AcceptedBook is { } b ? new BookDto(b.Id, b.Title, b.Author, b.Source, b.ExternalId, b.ImportedAt) : null,
        job.Events.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Select(e => new ImportEventDto(e.Message, e.CreatedAt)).ToList(),
        new SourceCleanupDto(job.CleanupStatus.ToString(), job.CleanupAttemptCount, job.CleanupError,
            cleanupEnabled && job.CleanupStatus == SourceCleanupStatus.Retained));
}

public sealed record SourceCleanupDto(string Status, int AttemptCount, string? Error, bool CanRetry);
public sealed record MetadataCandidateDto(long Id, string Title, string Author, string Source, string ExternalId, decimal ConfidenceScore, string Evidence);
public sealed record BookDto(long Id, string Title, string Author, string Source, string ExternalId, DateTime ImportedAt);
public sealed record ImportEventDto(string Message, DateTime CreatedAt);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record ImportPreviewDto(string RelativePath, bool DestinationExists, bool SourceRetained);
public sealed record DuplicateSourceDto(long Id, string FileName, string? RelativePath, DateTime DiscoveredAt);
public sealed record EditMetadataRequest(
    [Required, StringLength(300, MinimumLength = 1)] string Title,
    [Required, StringLength(300, MinimumLength = 1)] string Author);
