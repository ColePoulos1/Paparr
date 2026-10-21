namespace Paparr.API.Domain;

public class Book
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public required string Source { get; set; }
    public required string ExternalId { get; set; }
    public string? FilePath { get; set; }
    public DateTime ImportedAt { get; set; }
    public IntegrityStatus IntegrityStatus { get; set; }
    public DateTime? IntegrityCheckedAt { get; set; }
    public string? IntegrityMessage { get; set; }

    public long? ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }
}
