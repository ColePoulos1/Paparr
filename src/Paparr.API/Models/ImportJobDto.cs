namespace Paparr.API.Models;

public class ImportJobDto
{
    public long Id { get; set; }
    public required string FilePath { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<MetadataCandidateDto> Candidates { get; set; } = new();
    public BookDto? AcceptedBook { get; set; }
}

public class MetadataCandidateDto
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public required string Source { get; set; }
    public required string ExternalId { get; set; }
    public decimal ConfidenceScore { get; set; }
}

public class BookDto
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public required string Source { get; set; }
    public required string ExternalId { get; set; }
    public DateTime ImportedAt { get; set; }
}

public class AcceptCandidateRequest
{
    public long CandidateId { get; set; }
}
