using System.IO.Compression;
using System.Security;
using System.Text;
using Paparr.TestFixtures;

namespace Paparr.IngestionBenchmarks;

public static class BenchmarkFixtures
{
    public static string[] Create(string ingest, BenchmarkOptions settings)
    {
        if (settings.Files is < 2 or > 100 || settings.ContentMiB is < 0 or > 4 ||
            settings.RequestedContentBytes > BenchmarkOptions.MaximumInputBytes)
            throw new ArgumentOutOfRangeException(nameof(settings));
        var paths = new string[settings.Files];
        var actualBytes = 0L;
        for (var index = 0; index < settings.Files; index++)
        {
            var title = $"Benchmark Book {index + 1:D3}";
            var author = $"Benchmark Author {index + 1:D3}";
            var extension = index % 2 == 0 ? ".pdf" : ".epub";
            var path = Path.Combine(ingest, $"synthetic-{index + 1:D3}{extension}");
            if (extension == ".pdf") PdfFixture.Write(path, title, author, settings.ContentBytesPerFile);
            else WriteEpub(path, title, author, settings.ContentBytesPerFile);
            actualBytes += new FileInfo(path).Length;
            if (actualBytes > BenchmarkOptions.MaximumInputBytes)
                throw new InvalidDataException("Generated inputs exceed the 512 MiB limit.");
            paths[index] = path;
        }
        return paths;
    }

    public static void WriteEpub(string path, string title, string author, int contentBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(contentBytes, 4 * 1024 * 1024);
        string[] chapters = contentBytes == 0 ? ["chapter.xhtml"]
            : Enumerable.Range(1, (contentBytes + 65535) / 65536).Select(index => $"chapters/chapter-{index:D3}.xhtml").ToArray();
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Add(archive, "META-INF/container.xml",
            """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
        var manifest = string.Concat(chapters.Select((chapter, index) => $"<item id=\"chapter{index}\" href=\"{chapter}\" media-type=\"application/xhtml+xml\"/>"));
        var spine = string.Concat(chapters.Select((_, index) => $"<itemref idref=\"chapter{index}\"/>"));
        Add(archive, "content.opf",
            $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:uuid:{Guid.NewGuid():D}</dc:identifier><dc:title>{SecurityElement.Escape(title)}</dc:title><dc:creator>{SecurityElement.Escape(author)}</dc:creator><dc:language>en</dc:language><meta property="dcterms:modified">2026-01-01T00:00:00Z</meta></metadata><manifest>{manifest}<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest><spine>{spine}</spine></package>""");
        var paragraph = 0;
        for (var index = 0; index < chapters.Length; index++)
        {
            if (contentBytes == 0)
            {
                Add(archive, chapters[index], "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Offline benchmark fixture.</p></body></html>");
                continue;
            }
            var chapter = new StringBuilder("<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Benchmark chapter</title></head><body>\n");
            var target = Math.Min(65536, contentBytes - index * 65536);
            while (chapter.Length < target)
                chapter.Append($"<p>Benchmark paragraph {++paragraph:D8}: This generated text is readable content for offline import measurements.</p>\n");
            chapter.Append("</body></html>");
            Add(archive, chapters[index], chapter.ToString(), CompressionLevel.NoCompression);
        }
        var navigation = string.Concat(chapters.Select((chapter, index) => $"<li><a href=\"{chapter}\">Chapter {index + 1}</a></li>"));
        Add(archive, "nav.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol>{navigation}</ol></nav></body></html>");
    }

    private static void Add(ZipArchive archive, string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path, compression).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
