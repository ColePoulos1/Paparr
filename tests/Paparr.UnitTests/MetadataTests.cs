using System.IO.Compression;
using Paparr.API.Services;
using Paparr.TestFixtures;

namespace Paparr.UnitTests;

public sealed class MetadataTests
{
    private readonly MetadataService service = new();

    [Fact]
    public async Task Reads_metadata_from_an_actual_pdf()
    {
        using var library = new TemporaryLibrary();
        var path = Path.Combine(library.Ingest, "book.pdf");
        PdfFixture.Write(path, "The Time Machine", "H. G. Wells");
        Assert.Equal(("The Time Machine", "H. G. Wells"), await service.ExtractPdfMetadataAsync(path));
    }

    [Fact]
    public async Task Pdf_without_metadata_allows_filename_fallback()
    {
        using var library = new TemporaryLibrary();
        var path = Path.Combine(library.Ingest, "The Time Machine - H. G. Wells.pdf");
        PdfFixture.Write(path);
        Assert.Null(await service.ExtractPdfMetadataAsync(path));
        Assert.Equal(("The Time Machine", "H. G. Wells"), service.ParseFilename(Path.GetFileName(path)));
    }

    [Fact]
    public async Task Reads_metadata_from_an_actual_epub()
    {
        using var library = new TemporaryLibrary();
        var path = Path.Combine(library.Ingest, "book.epub");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "mimetype", "application/epub+zip");
            Add(zip, "META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            Add(zip, "content.opf", """<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">test</dc:identifier><dc:title>Émile's Book</dc:title><dc:creator>Émile Zola</dc:creator><dc:language>en</dc:language></metadata><manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest><spine><itemref idref="chapter"/></spine></package>""");
            Add(zip, "chapter.xhtml", """<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter</title></head><body><p>Fixture.</p></body></html>""");
            Add(zip, "nav.xhtml", """<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="chapter.xhtml">Chapter</a></li></ol></nav></body></html>""");
        }
        Assert.Equal(("Émile's Book", "Émile Zola"), await service.ExtractEpubMetadataAsync(path));
    }

    [Theory]
    [InlineData("Twenty-One Lessons - Anne-Marie Smith.epub", "Twenty-One Lessons", "Anne-Marie Smith")]
    [InlineData("Title - Subtitle - Author.pdf", "Title - Subtitle", "Author")]
    public void Filename_parsing_preserves_internal_hyphens(string filename, string title, string author) =>
        Assert.Equal((title, author), service.ParseFilename(filename));

    [Theory]
    [InlineData("unstructured.pdf")]
    [InlineData("Title - .epub")]
    [InlineData(" - Author.epub")]
    public void Incomplete_filenames_do_not_invent_metadata(string filename) => Assert.Null(service.ParseFilename(filename));

    [Fact]
    public void Matching_normalizes_accents_and_case()
    {
        var match = MetadataMatcher.Compare("L'Étranger", "Albert Camus", "l'etranger", "ALBERT CAMUS");
        Assert.Equal(100, match.Score);
        Assert.Contains("Title match 100/100", match.Evidence);
    }

    [Fact]
    public void Missing_fields_are_not_a_perfect_match() => Assert.Equal(0, MetadataMatcher.Compare("", "", "", "").Score);

    private static void Add(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(content);
    }
}
