using System.IO.Compression;
using UglyToad.PdfPig;
using VersOne.Epub;

namespace Paparr.API.Services;

public sealed class MetadataService : IMetadataService
{
    public async Task<(string Title, string Author)?> ExtractEpubMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var archive = ZipFile.OpenRead(filePath))
        {
            if (archive.Entries.Count > 10000 || archive.Entries.Sum(e => e.Length) > 200L * 1024 * 1024)
                throw new InvalidDataException("The EPUB exceeds the archive limits.");
        }
        var book = await EpubReader.ReadBookAsync(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        return Complete(book.Title, string.Join(", ", book.AuthorList ?? []));
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
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author)) return null;
        return (title.Trim(), author.Trim());
    }
}
