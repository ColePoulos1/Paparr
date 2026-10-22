using System.Text;

namespace Paparr.TestFixtures;

public static class PdfFixture
{
    public static void Write(string path, string title = "", string author = "", int contentBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(contentBytes, 4 * 1024 * 1024);
        if (contentBytes > 0)
        {
            WriteTextPages(path, title, author, contentBytes);
            return;
        }
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
            $"<< /Title ({Escape(title)}) /Author ({Escape(author)}) /Subject ({Guid.NewGuid():N}) >>"
        };
        WriteDocument(path, objects, 4);
    }

    private static void WriteTextPages(string path, string title, string author, int contentBytes)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "",
            $"<< /Title ({Escape(title)}) /Author ({Escape(author)}) /Subject ({Guid.NewGuid():N}) >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        var pageReferences = new List<string>();
        var writtenContent = 0;
        var line = 0;
        while (writtenContent < contentBytes)
        {
            var content = new StringBuilder("BT\n/F1 9 Tf\n36 756 Td\n");
            for (var row = 0; row < 60; row++)
                content.Append($"(Benchmark text {++line:D8}: This synthetic paragraph is visible page content.) Tj\n0 -12 Td\n");
            content.Append("ET\n");
            var pageId = objects.Count + 1;
            pageReferences.Add($"{pageId} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents {pageId + 1} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
            writtenContent += content.Length;
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', pageReferences)}] /Count {pageReferences.Count} >>";
        WriteDocument(path, objects, 3);
    }

    private static void WriteDocument(string path, IReadOnlyList<string> objects, int infoId)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(output.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info {infoId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
