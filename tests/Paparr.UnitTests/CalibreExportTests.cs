using System.Xml.Linq;
using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class CalibreExportTests
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    [Fact]
    public async Task Opf_escapes_metadata_and_writes_only_normalized_optional_values()
    {
        var book = Book("Title <&> \"Edition\" \u0001 " + new string('L', 301), "Åuthor & Co 😀");
        book.Isbn13 = "978-0-306-40615-7";
        book.IsbnStatus = EditionIsbnStatus.Valid;
        book.Publisher = "Pub\u0001 & Co <Press>";
        book.PublicationYear = 2001;
        book.Language = "EN-us";
        await using var output = new MemoryStream();

        await CalibreExportService.WriteOpfAsync(output, book, "book.epub");
        output.Position = 0;
        var xml = await XDocument.LoadAsync(output, LoadOptions.None, default);

        Assert.Equal("Title <&> \"Edition\"  " + new string('L', 301), (string?)xml.Root?.Element(Opf + "metadata")?.Element(Dc + "title"));
        Assert.Equal("Åuthor & Co 😀", (string?)xml.Root?.Element(Opf + "metadata")?.Element(Dc + "creator"));
        Assert.Equal("9780306406157", (string?)xml.Root?.Element(Opf + "metadata")?.Elements(Dc + "identifier").Single());
        Assert.Equal("Pub & Co <Press>", (string?)xml.Root?.Element(Opf + "metadata")?.Element(Dc + "publisher"));
        Assert.Equal("2001", (string?)xml.Root?.Element(Opf + "metadata")?.Element(Dc + "date"));
        Assert.Equal("en-us", (string?)xml.Root?.Element(Opf + "metadata")?.Element(Dc + "language"));
        Assert.Equal("book.epub", (string?)xml.Root?.Element(Opf + "manifest")?.Element(Opf + "item")?.Attribute("href"));
    }

    [Fact]
    public async Task Opf_omits_missing_or_invalid_edition_metadata_and_keeps_year_as_year()
    {
        var book = Book("Plain title", "Plain author");
        book.Isbn13 = "not-an-isbn";
        book.IsbnStatus = EditionIsbnStatus.Invalid;
        book.PublicationYear = 999;
        book.Language = "not a language";
        await using var output = new MemoryStream();

        await CalibreExportService.WriteOpfAsync(output, book, "book.pdf");
        output.Position = 0;
        var xml = await XDocument.LoadAsync(output, LoadOptions.None, default);
        var metadata = xml.Root!.Element(Opf + "metadata")!;

        Assert.Empty(metadata.Elements(Dc + "publisher"));
        Assert.Empty(metadata.Elements(Dc + "date"));
        Assert.Empty(metadata.Elements(Dc + "language"));
        Assert.DoesNotContain(metadata.Elements(Dc + "identifier"), identifier => (string?)identifier.Attribute(Opf + "scheme") == "ISBN");
        Assert.Equal("book.pdf", (string?)xml.Root.Element(Opf + "manifest")?.Element(Opf + "item")?.Attribute("href"));
    }

    [Fact]
    public async Task Opf_rejects_unbounded_title_and_author_metadata()
    {
        var book = Book(new string('T', 64 * 1024), "Author");
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<CalibreExportMetadataException>(() =>
            CalibreExportService.WriteOpfAsync(output, book, "book.epub"));
    }

    private static Book Book(string title, string author) => new()
    {
        Id = 42,
        Title = title,
        Author = author,
        Source = "embedded",
        ExternalId = "",
        ImportedAt = DateTime.UtcNow
    };
}
