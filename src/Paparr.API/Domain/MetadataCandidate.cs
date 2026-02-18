namespace Paparr.API.Domain;

/// <summary>
/// Represents a metadata candidate for an import job.
/// Can come from file metadata, filename parsing, or API queries.
/// </summary>
public class MetadataCandidate
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public required string Source { get; set; } // e.g., "embedded", "filename", "openlibrary", "googlebooks"
    public required string ExternalId { get; set; }
    public decimal ConfidenceScore { get; set; } // 0-100, higher is better
    public DateTime CreatedAt { get; set; }

    // Foreign key
    public long ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }
}
