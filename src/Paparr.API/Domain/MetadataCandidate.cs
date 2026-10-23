namespace Paparr.API.Domain;

public class MetadataCandidate
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public required string Source { get; set; }
    public required string ExternalId { get; set; }
    public decimal ConfidenceScore { get; set; }
    public string Evidence { get; set; } = "";
    public string? Isbn13 { get; set; }
    public EditionIsbnStatus IsbnStatus { get; set; } = EditionIsbnStatus.NotProvided;
    public string? Publisher { get; set; }
    public int? PublicationYear { get; set; }
    public string? Language { get; set; }
    public DateTime CreatedAt { get; set; }

    public long ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }
}
