namespace Paparr.API.Domain;

public sealed class ImportUndo
{
    public Guid Id { get; set; }
    public long ImportJobId { get; set; }
    public long BookId { get; set; }
    public required string SourcePath { get; set; }
    public required string DestinationPath { get; set; }
    public required string FileHash { get; set; }
    public ImportUndoStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? RetryAfter { get; set; }
    public string? Error { get; set; }
}

public enum ImportUndoStatus { Pending, Retained, Completed }
