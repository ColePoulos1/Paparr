using VersOne.Epub;
using System.IO.Compression;
using System.Xml.XPath;

namespace Paparr.API.Services;

public class MetadataService : IMetadataService
{
    private readonly ILogger<MetadataService> _logger;

    public MetadataService(ILogger<MetadataService> logger)
    {
        _logger = logger;
    }

    public async Task<(string Title, string Author)?> ExtractEpubMetadataAsync(string filePath)
    {
        try
        {
            var book = await EpubReader.ReadBookAsync(filePath);
            
            var title = book.Title ?? "Unknown";
            var author = string.Join(", ", book.AuthorList ?? Array.Empty<string>()) ?? "Unknown";

            return (title, author);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract EPUB metadata from {FilePath}", filePath);
            return null;
        }
    }

    public async Task<(string Title, string Author)?> ExtractPdfMetadataAsync(string filePath)
    {
        try
        {
            // Basic PDF metadata extraction using System.IO.Compression
            // In production, consider using more robust libraries like PdfSharpCore
            using var zipArchive = new ZipArchive(File.OpenRead(filePath), ZipArchiveMode.Read, true);
            
            var metadataEntry = zipArchive.Entries.FirstOrDefault(e => e.Name == "metadata.xml");
            
            if (metadataEntry != null)
            {
                using var stream = metadataEntry.Open();
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                
                // Simple regex extraction - in production use proper XML parsing
                var titleMatch = System.Text.RegularExpressions.Regex.Match(content, @"<dc:title>([^<]+)</dc:title>");
                var authorMatch = System.Text.RegularExpressions.Regex.Match(content, @"<dc:creator>([^<]+)</dc:creator>");
                
                if (titleMatch.Success && authorMatch.Success)
                {
                    return (titleMatch.Groups[1].Value, authorMatch.Groups[1].Value);
                }
            }

            _logger.LogDebug("No metadata found in PDF {FilePath}", filePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract PDF metadata from {FilePath}", filePath);
            return null;
        }
    }

    public (string Title, string Author)? ParseFilename(string filename)
    {
        try
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
            var parts = nameWithoutExt.Split(new[] { " - ", "-" }, StringSplitOptions.None);

            if (parts.Length >= 2)
            {
                var title = parts[0].Trim();
                var author = string.Join("-", parts.Skip(1)).Trim();
                return (title, author);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse filename {Filename}", filename);
            return null;
        }
    }
}
