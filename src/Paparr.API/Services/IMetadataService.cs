using Paparr.API.Domain;

namespace Paparr.API.Services;

public interface IMetadataService
{
    Task<EmbeddedEpubMetadata?> ExtractEpubMetadataAsync(string filePath, CancellationToken cancellationToken = default);
    Task<(string Title, string Author)?> ExtractPdfMetadataAsync(string filePath, CancellationToken cancellationToken = default);
    (string Title, string Author)? ParseFilename(string filename);
    (string Title, string Author)? ParseFilename(string filename, FilenameOrder order);
}

public sealed record EmbeddedEpubMetadata(string Title, string Author, IReadOnlyList<string> Isbns, string? Publisher,
    string? PublicationDate, string? Language);
