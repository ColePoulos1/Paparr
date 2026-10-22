using System.IO.Compression;
using UglyToad.PdfPig;
using VersOne.Epub;

namespace Paparr.API.Services;

public sealed class MetadataService : IMetadataService
{
    public async Task<EmbeddedEpubMetadata?> ExtractEpubMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var archive = ZipFile.OpenRead(filePath))
        {
            if (archive.Entries.Count > 10000 || archive.Entries.Sum(e => e.Length) > 200L * 1024 * 1024)
                throw new InvalidDataException("The EPUB exceeds the archive limits.");
        }
        var book = await EpubReader.ReadBookAsync(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var complete = Complete(book.Title, string.Join(", ", book.AuthorList ?? []));
        if (!complete.HasValue) return null;
        var metadata = book.Schema?.Package?.Metadata;
        var identifiers = metadata?.Identifiers?
            .Where(identifier => string.Equals(identifier.Scheme, "ISBN", StringComparison.OrdinalIgnoreCase) ||
                (identifier.Id?.Contains("isbn", StringComparison.OrdinalIgnoreCase) ?? false) ||
                identifier.Identifier.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase))
            .Select(identifier => identifier.Identifier).ToList() ?? [];
        var publishers = metadata?.Publishers?.Select(item => item.Publisher).ToArray() ?? [];
        var publicationDates = metadata?.Dates?
            .Where(item => string.Equals(item.Event, "publication", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Date).ToArray() ?? [];
        if (publicationDates.Length == 0 && metadata?.Dates?.Count == 1)
            publicationDates = [metadata.Dates[0].Date];
        var languages = metadata?.Languages?.Select(item => item.Language).ToArray() ?? [];
        return new EmbeddedEpubMetadata(complete.Value.Title, complete.Value.Author, identifiers,
            publishers.Length == 1 ? publishers[0] : null,
            publicationDates.Length == 1 ? publicationDates[0] : null,
            languages.Length == 1 ? languages[0] : null);
    }

    public Task<(string Title, string Author)?> ExtractPdfMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(filePath);
        return Task.FromResult(Complete(document.Information.Title, document.Information.Author));
    }

    public (string Title, string Author)? ParseFilename(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        var separator = stem.LastIndexOf(" - ", StringComparison.Ordinal);
        return separator <= 0 ? null : Complete(stem[..separator], stem[(separator + 3)..]);
    }

    private static (string Title, string Author)? Complete(string? title, string? author)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author) ||
            title.Contains('\0') || author.Contains('\0')) return null;
        return (title.Trim(), author.Trim());
    }
}
