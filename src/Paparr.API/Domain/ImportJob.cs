namespace Paparr.API.Domain;

/// <summary>
/// Represents a single import operation for an ebook file.
/// </summary>
public class ImportJob
{
    public long Id { get; set; }
    public required string FilePath { get; set; }
    public required string FileHash { get; set; }
    public ImportStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation properties
    public ICollection<MetadataCandidate> Candidates { get; set; } = new List<MetadataCandidate>();
    public Book? AcceptedBook { get; set; }
}

public enum ImportStatus
{
    Pending,
    Processing,
    AwaitingApproval,
    Completed,
    Failed
}
