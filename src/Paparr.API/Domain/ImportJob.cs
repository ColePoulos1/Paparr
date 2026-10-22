namespace Paparr.API.Domain;

public class ImportJob
{
    public long Id { get; set; }
    public required string FilePath { get; set; }
    public required string FileHash { get; set; }
    public ImportStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public Guid? PublicationFence { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public long? SelectedCandidateId { get; set; }
    public string? DestinationPath { get; set; }
    public SourceCleanupStatus CleanupStatus { get; set; }
    public int CleanupAttemptCount { get; set; }
    public DateTime? CleanupAfter { get; set; }
    public string? CleanupError { get; set; }
    public ICollection<MetadataCandidate> Candidates { get; set; } = new List<MetadataCandidate>();
    public ICollection<ImportEvent> Events { get; set; } = new List<ImportEvent>();
    public ICollection<DuplicateSource> DuplicateSources { get; set; } = new List<DuplicateSource>();
    public Book? AcceptedBook { get; set; }
}

public enum ImportStatus { Pending, Processing, AwaitingApproval, Publishing, Completed, Failed }
public enum SourceCleanupStatus { NotRequested, Pending, Completed, Retained }
