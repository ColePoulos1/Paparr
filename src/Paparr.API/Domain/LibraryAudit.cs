namespace Paparr.API.Domain;

public sealed class LibraryAudit
{
    public long Id { get; set; }
    public AuditStatus Status { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid Revision { get; set; } = Guid.NewGuid();
    public long LastBookId { get; set; }
    public long MaximumBookId { get; set; }
    public int TotalBooks { get; set; }
    public int ProcessedBooks { get; set; }
    public int VerifiedBooks { get; set; }
    public int MissingBooks { get; set; }
    public int ChangedBooks { get; set; }
    public int UnavailableBooks { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
}
public enum AuditStatus { Queued, Running, Completed }
public enum IntegrityStatus { Unchecked, Verified, Missing, Changed, Unavailable }
