using Paparr.API.Domain;

namespace Paparr.API.Models;

public sealed record LibraryBookDto(long Id, string Title, string Author, string? RelativePath,
    DateTime ImportedAt, string IntegrityStatus, DateTime? CheckedAt, string? Message);
public sealed record LibraryPageDto(IReadOnlyList<LibraryBookDto> Items, int Total, int Page, int PageSize,
    IReadOnlyDictionary<string, int> Counts);
public sealed record LibraryAuditDto(long Id, string Status, int TotalBooks, int ProcessedBooks, int VerifiedBooks,
    int MissingBooks, int ChangedBooks, int UnavailableBooks, DateTime CreatedAt, DateTime? CompletedAt)
{
    public static LibraryAuditDto From(LibraryAudit audit) => new(audit.Id, audit.Status.ToString(), audit.TotalBooks,
        audit.ProcessedBooks, audit.VerifiedBooks, audit.MissingBooks, audit.ChangedBooks, audit.UnavailableBooks,
        audit.CreatedAt, audit.CompletedAt);
}
