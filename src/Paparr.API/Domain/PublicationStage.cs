namespace Paparr.API.Domain;

public sealed class PublicationStage
{
    public Guid Id { get; set; }
    public long ImportJobId { get; set; }
    public Guid PublicationFence { get; set; }
    public required string FilePath { get; set; }
    public required string DestinationPath { get; set; }
    public required string FileHash { get; set; }
    public PublicationStageStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int CleanupAttemptCount { get; set; }
    public DateTime? CleanupAfter { get; set; }
    public string? CleanupError { get; set; }
}

public enum PublicationStageStatus { Planned, Owned, Published, Cleaned, Retained }
