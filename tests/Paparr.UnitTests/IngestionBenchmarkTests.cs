using System.IO.Compression;
using System.Xml.Linq;
using Paparr.API.Services;
using Paparr.IngestionBenchmarks;
using Paparr.TestFixtures;
using UglyToad.PdfPig;
using VersOne.Epub;

namespace Paparr.UnitTests;

public sealed class IngestionBenchmarkTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Larger_pdfs_have_real_text_pages_and_readable_embedded_metadata(int contentMiB)
    {
        using var library = new TemporaryLibrary();
        var path = Path.Combine(library.Ingest, "larger.pdf");
        var requestedBytes = contentMiB * 1024 * 1024;
        PdfFixture.Write(path, "Bounded (readable) PDF", "Fixture Author", requestedBytes);

        using var document = PdfDocument.Open(path, ParsingOptions.LenientParsingOff);
        Assert.Equal("Bounded (readable) PDF", document.Information.Title);
        Assert.Equal("Fixture Author", document.Information.Author);
        Assert.True(document.NumberOfPages > 1);
        Assert.Contains("Benchmark text 00000001", document.GetPage(1).Text);
        Assert.Contains("visible page content", document.GetPage(document.NumberOfPages).Text);
        Assert.Equal(("Bounded (readable) PDF", "Fixture Author"), await new MetadataService().ExtractPdfMetadataAsync(path));
        Assert.InRange(new FileInfo(path).Length, requestedBytes, requestedBytes + requestedBytes / 5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Larger_epubs_store_renderable_chapters_in_the_manifest_and_reading_order(int contentMiB)
    {
        using var library = new TemporaryLibrary();
        var path = Path.Combine(library.Ingest, "larger.epub");
        var requestedBytes = contentMiB * 1024 * 1024;
        const string title = "A <bounded> & valid book";
        BenchmarkFixtures.WriteEpub(path, title, "Fixture Author", requestedBytes);

        var book = await EpubReader.ReadBookAsync(path);
        Assert.Equal(title, book.Title);
        Assert.Equal("Fixture Author", Assert.Single(book.AuthorList));
        Assert.True(book.ReadingOrder.Count > 1);
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        foreach (var chapter in book.ReadingOrder)
        {
            var document = XDocument.Parse(chapter.Content);
            var paragraphs = document.Descendants(xhtml + "p").ToList();
            Assert.NotEmpty(paragraphs);
            Assert.All(paragraphs, paragraph => Assert.Contains("Benchmark paragraph", paragraph.Value));
        }
        using var archive = ZipFile.OpenRead(path);
        Assert.Equal("mimetype", archive.Entries[0].FullName);
        var entries = archive.Entries.Where(entry => entry.FullName.StartsWith("chapters/", StringComparison.Ordinal)).ToList();
        Assert.Equal(book.ReadingOrder.Count, entries.Count);
        Assert.True(entries.Sum(entry => entry.Length) >= requestedBytes);
        Assert.All(entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
        var metadata = await new MetadataService().ExtractEpubMetadataAsync(path);
        Assert.NotNull(metadata);
        Assert.Equal((title, "Fixture Author"), (metadata.Title, metadata.Author));
        Assert.InRange(new FileInfo(path).Length, requestedBytes, requestedBytes + requestedBytes / 5);
    }

    [Fact]
    public async Task Default_options_keep_the_small_mixed_metadata_fixtures()
    {
        using var library = new TemporaryLibrary();
        var defaults = BenchmarkArguments.Parse([]);
        Assert.Null(defaults.Error);
        Assert.NotNull(defaults.Options);
        Assert.Equal(10, defaults.Options.Files);
        Assert.Equal(0, defaults.Options.ContentMiB);
        var paths = BenchmarkFixtures.Create(library.Ingest, defaults.Options with { Files = 2 });
        Assert.Equal(new[] { ".pdf", ".epub" }, paths.Select(Path.GetExtension));
        Assert.True(paths.Sum(path => new FileInfo(path).Length) < 16 * 1024);
        Assert.Equal(("Benchmark Book 001", "Benchmark Author 001"), await new MetadataService().ExtractPdfMetadataAsync(paths[0]));
        var epub = await new MetadataService().ExtractEpubMetadataAsync(paths[1]);
        Assert.NotNull(epub);
        Assert.Equal(("Benchmark Book 002", "Benchmark Author 002"), (epub.Title, epub.Author));
        using var pdf = PdfDocument.Open(paths[0], ParsingOptions.LenientParsingOff);
        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Empty(pdf.GetPage(1).Text);
    }

    [Theory]
    [InlineData("2", "0", 2, 0)]
    [InlineData("100", "4", 100, 4)]
    public void Arguments_accept_bounded_workloads_and_preserve_the_output_path(string files, string contentMiB,
        int expectedFiles, int expectedContentMiB)
    {
        var parsed = BenchmarkArguments.Parse(["--content-mib", contentMiB, "--output", "new result.json", "--files", files]);
        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Options);
        Assert.Equal(expectedFiles, parsed.Options.Files);
        Assert.Equal(expectedContentMiB, parsed.Options.ContentMiB);
        Assert.Equal("new result.json", parsed.Options.Output);
        Assert.True(parsed.Options.RequestedContentBytes <= BenchmarkOptions.MaximumInputBytes);
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { "--files", "1" }, new[] { "--files", "101" }, new[] { "--files", "-2" },
        new[] { "--files", "2147483648" }, new[] { "--content-mib", "-1" }, new[] { "--content-mib", "5" },
        new[] { "--content-mib", "1.5" }, new[] { "--content-mib", "NaN" }, new[] { "--content-mib" },
        new[] { "--output", " " }, new[] { "--unknown", "1" },
        new[] { "--files", "2", "--files", "3" }, new[] { "--content-mib", "1", "--content-mib", "2" },
        new[] { "--output", "first.json", "--output", "second.json" }
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Arguments_reject_invalid_missing_unknown_and_duplicate_values(string[] args)
    {
        var parsed = BenchmarkArguments.Parse(args);
        Assert.Null(parsed.Options);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Error));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4194305)]
    public void Invalid_fixture_content_sizes_leave_the_output_path_uncreated(int bytes)
    {
        using var library = new TemporaryLibrary();
        var pdf = Path.Combine(library.Ingest, "invalid.pdf");
        var epub = Path.Combine(library.Ingest, "invalid.epub");
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfFixture.Write(pdf, contentBytes: bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkFixtures.WriteEpub(epub, "Title", "Author", bytes));
        Assert.Empty(Directory.GetFiles(library.Ingest));
    }
}
