using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed record CalibreExport(Stream Stream, string FileName, string ContentType);

public sealed class CalibreExportMetadataException() : Exception("Approved book metadata is too large to export.");

public sealed class CalibreExportService(AppDbContext db, LibraryDownloadService downloads)
{
    private const string OpfNamespace = "http://www.idpf.org/2007/opf";
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";
    private const int MaxTitleAndAuthorCharacters = 64 * 1024;
    private const string Readme = """
Paparr Calibre handoff

Extract this archive into a new directory. The book file is a copy; metadata.opf contains the approved Paparr metadata.

To apply the metadata to the exported copy, run one of these commands from the extracted directory:
  ebook-meta book.epub --from-opf metadata.opf
  ebook-meta book.pdf --from-opf metadata.opf

Then add the book using Calibre's GUI, or run:
  calibredb add book.epub --with-library PATH
  calibredb add book.pdf --with-library PATH

Replace PATH with your Calibre library location and use the filename matching this export. These steps only modify the exported copy and the Calibre library you select. Paparr creates this archive in memory; the configured maximum book file size applies (100 MB by default), and title plus author metadata is limited to 64K characters.

Calibre command references:
https://manual.calibre-ebook.com/generated/en/ebook-meta.html#cmdoption-ebook-meta-from-opf
https://manual.calibre-ebook.com/generated/en/calibredb.html#add
""";

    public async Task<CalibreExport?> CreateAsync(long id, CancellationToken cancellationToken)
    {
        var book = await db.Books.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null) return null;

        var verified = await downloads.OpenVerifiedAsync(id, cancellationToken);
        if (verified is null) return null;

        await using var source = verified.Stream;
        var extension = Path.GetExtension(verified.FileName).ToLowerInvariant();
        var bookEntryName = extension switch
        {
            ".epub" => "book.epub",
            ".pdf" => "book.pdf",
            _ => throw new LibraryDownloadConflictException("The stored book file is unavailable or has changed.")
        };

        // OpenVerifiedAsync enforces MaxFileSizeMb before the source is copied into this in-memory archive.
        var archiveStream = new MemoryStream();
        try
        {
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var bookEntry = archive.CreateEntry(bookEntryName, CompressionLevel.NoCompression);
                bookEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using (var destination = bookEntry.Open())
                    await source.CopyToAsync(destination, 81920, cancellationToken);

                var opfEntry = archive.CreateEntry("metadata.opf", CompressionLevel.NoCompression);
                opfEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using (var destination = opfEntry.Open())
                    await WriteOpfAsync(destination, book, bookEntryName, cancellationToken);

                var readmeEntry = archive.CreateEntry("README.txt", CompressionLevel.NoCompression);
                readmeEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using (var destination = readmeEntry.Open())
                await using (var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true))
                    await writer.WriteAsync(Readme.AsMemory(), cancellationToken);
            }

            archiveStream.Position = 0;
            return new CalibreExport(archiveStream, $"paparr-book-{book.Id}.zip", "application/zip");
        }
        catch
        {
            await archiveStream.DisposeAsync();
            throw;
        }
    }

    public static async Task WriteOpfAsync(Stream destination, Book book, string bookEntryName,
        CancellationToken cancellationToken = default)
    {
        var settings = new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false };
        using var writer = XmlWriter.Create(destination, settings);
        if (book.Title.Length + (long)book.Author.Length > MaxTitleAndAuthorCharacters)
            throw new CalibreExportMetadataException();
        var title = SafeXmlText(book.Title).Trim();
        var author = SafeXmlText(book.Author).Trim();

        var isbn = book.IsbnStatus == EditionIsbnStatus.Valid ? EditionMetadataNormalizer.NormalizeIsbn(book.Isbn13) : null;
        var publisher = Bound(book.Publisher, 300);
        var language = EditionMetadataNormalizer.CreateManual(null, null, null, book.Language).Language;
        var year = book.PublicationYear is >= 1000 and <= 9999
            ? book.PublicationYear.Value.ToString(CultureInfo.InvariantCulture) : null;

        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "package", OpfNamespace);
        await writer.WriteAttributeStringAsync(null, "version", null, "2.0");
        await writer.WriteAttributeStringAsync(null, "unique-identifier", null, "bookid");
        await writer.WriteStartElementAsync(null, "metadata", OpfNamespace);
        await writer.WriteAttributeStringAsync("xmlns", "dc", null, DcNamespace);
        await writer.WriteAttributeStringAsync("xmlns", "opf", null, OpfNamespace);
        await writer.WriteElementStringAsync("dc", "title", DcNamespace, title.Length == 0 ? "Untitled" : title);
        await writer.WriteStartElementAsync("dc", "creator", DcNamespace);
        await writer.WriteAttributeStringAsync("opf", "role", OpfNamespace, "aut");
        await writer.WriteStringAsync(author.Length == 0 ? "Unknown author" : author);
        await writer.WriteEndElementAsync();
        if (isbn is not null)
        {
            await writer.WriteStartElementAsync("dc", "identifier", DcNamespace);
            await writer.WriteAttributeStringAsync(null, "id", null, "bookid");
            await writer.WriteAttributeStringAsync("opf", "scheme", OpfNamespace, "ISBN");
            await writer.WriteStringAsync(isbn);
            await writer.WriteEndElementAsync();
        }
        else
        {
            await writer.WriteStartElementAsync("dc", "identifier", DcNamespace);
            await writer.WriteAttributeStringAsync(null, "id", null, "bookid");
            await writer.WriteStringAsync($"urn:paparr:book:{book.Id}");
            await writer.WriteEndElementAsync();
        }
        if (publisher is not null) await writer.WriteElementStringAsync("dc", "publisher", DcNamespace, publisher);
        if (year is not null) await writer.WriteElementStringAsync("dc", "date", DcNamespace, year);
        if (language is not null) await writer.WriteElementStringAsync("dc", "language", DcNamespace, language);
        await writer.WriteEndElementAsync();
        await writer.WriteStartElementAsync(null, "manifest", OpfNamespace);
        await writer.WriteStartElementAsync(null, "item", OpfNamespace);
        await writer.WriteAttributeStringAsync(null, "id", null, "book");
        await writer.WriteAttributeStringAsync(null, "href", null, bookEntryName);
        await writer.WriteAttributeStringAsync(null, "media-type", null,
            bookEntryName.EndsWith(".epub", StringComparison.Ordinal) ? "application/epub+zip" : "application/pdf");
        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        await writer.WriteStartElementAsync(null, "spine", OpfNamespace);
        await writer.WriteStartElementAsync(null, "itemref", OpfNamespace);
        await writer.WriteAttributeStringAsync(null, "idref", null, "book");
        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string? Bound(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum ? SafeXmlText(value).Trim() : null;

    private static string SafeXmlText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    result.Append(character).Append(value[++index]);
                }
                continue;
            }
            if (!char.IsLowSurrogate(character) && XmlConvert.IsXmlChar(character)) result.Append(character);
        }
        return result.ToString();
    }
}
