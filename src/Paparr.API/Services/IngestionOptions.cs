using System.ComponentModel.DataAnnotations;
using ProfileRules = Paparr.API.Services.FolderProfiles;

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
    public List<FolderProfileOptions> FolderProfiles { get; set; } = [];

    public bool HasValidFolderProfiles()
    {
        if (!ProfileRules.AreValid(FolderProfiles)) return false;
        try
        {
            foreach (var profile in FolderProfiles)
            {
                var folder = ProfileRules.NormalizeSubfolder(profile.IngestSubfolder)
                    .Replace('/', Path.DirectorySeparatorChar);
                LibraryPaths.EnsureSafe(IngestPath, Path.Combine(IngestPath, folder));
                LibraryPaths.EnsureSafe(LibraryPath, profile.Collection is null ? LibraryPath : Path.Combine(LibraryPath, profile.Collection));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public bool HasSeparateRoots()
    {
        var ingest = Path.GetFullPath(IngestPath);
        var library = Path.GetFullPath(LibraryPath);
        return !LibraryPaths.IsWithin(ingest, library) && !LibraryPaths.IsWithin(library, ingest);
    }
}
