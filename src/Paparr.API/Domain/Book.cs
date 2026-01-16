namespace Paparr.API.Domain;

/// <summary>
/// Represents an accepted book in the library.
/// </summary>
public class Book
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Author { get; set; }
    public required string Source { get; set; } // e.g., "openlibrary", "googlebooks"
    public required string ExternalId { get; set; }
    public string? FilePath { get; set; }
    public DateTime ImportedAt { get; set; }

    // Foreign key
    public long? ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }
}
