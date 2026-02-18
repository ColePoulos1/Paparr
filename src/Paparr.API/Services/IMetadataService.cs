namespace Paparr.API.Services;

public interface IMetadataService
{
    /// <summary>
    /// Extracts metadata from an EPUB file.
    /// </summary>
    Task<(string Title, string Author)?> ExtractEpubMetadataAsync(string filePath);

    /// <summary>
    /// Extracts metadata from a PDF file.
    /// </summary>
    Task<(string Title, string Author)?> ExtractPdfMetadataAsync(string filePath);

    /// <summary>
    /// Parses filename to extract title and author.
    /// Assumes format: "Title - Author.ext"
    /// </summary>
    (string Title, string Author)? ParseFilename(string filename);
}
