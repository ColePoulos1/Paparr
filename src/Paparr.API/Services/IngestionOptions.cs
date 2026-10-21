using System.ComponentModel.DataAnnotations;

namespace Paparr.API.Services;

public sealed class IngestionOptions
{
    [Required] public string IngestPath { get; set; } = "./ingest";
    [Required] public string LibraryPath { get; set; } = "./library";
    [Range(1, 3600)] public int PollingIntervalSeconds { get; set; } = 10;
    [Range(1, 300)] public int FileStableSeconds { get; set; } = 5;
    [Range(1, 1024)] public int MaxFileSizeMb { get; set; } = 100;
    [Range(1, 20)] public int MaxAttempts { get; set; } = 3;
    [Range(1, 60)] public int LeaseMinutes { get; set; } = 5;
    public bool DeleteSourceAfterImport { get; set; }
    public bool WorkerEnabled { get; set; } = true;
    public bool MetadataProvidersEnabled { get; set; } = true;

    public bool HasSeparateRoots()
    {
        var ingest = Path.GetFullPath(IngestPath);
        var library = Path.GetFullPath(LibraryPath);
        return !LibraryPaths.IsWithin(ingest, library) && !LibraryPaths.IsWithin(library, ingest);
    }
}
