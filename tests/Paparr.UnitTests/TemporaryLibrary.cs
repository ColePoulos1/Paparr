using Microsoft.Extensions.Options;
using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class TemporaryLibrary : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "paparr-tests", Guid.NewGuid().ToString("N"));
    public string Ingest => Path.Combine(Root, "ingest");
    public string Library => Path.Combine(Root, "library");
    public IngestionOptions Settings { get; }
    public TemporaryLibrary()
    {
        Directory.CreateDirectory(Ingest);
        Directory.CreateDirectory(Library);
        Settings = new() { IngestPath = Ingest, LibraryPath = Library };
    }
    public string Write(string content, string filename = "Book - Author.epub")
    {
        var path = Path.Combine(Ingest, filename);
        File.WriteAllText(path, content);
        return path;
    }
    public LibraryPublisher Publisher() => new(new FileHashService(), Options.Create(Settings));
    public void Dispose() => Directory.Delete(Root, true);
}
