namespace Paparr.API.Services;

public interface IMetadataService
{
    Task<(string Title, string Author)?> ExtractEpubMetadataAsync(string filePath, CancellationToken cancellationToken = default);
    Task<(string Title, string Author)?> ExtractPdfMetadataAsync(string filePath, CancellationToken cancellationToken = default);
    (string Title, string Author)? ParseFilename(string filename);
}
