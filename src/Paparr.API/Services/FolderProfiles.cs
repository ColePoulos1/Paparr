using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class FolderProfileOptions
{
    public string Name { get; set; } = "";
    public string IngestSubfolder { get; set; } = "";
    public string? Collection { get; set; }
    public FilenameOrder FilenameOrder { get; set; }
}

public sealed record FolderProfileSnapshot(string Name, string IngestSubfolder, string? Collection, FilenameOrder FilenameOrder);

public static class FolderProfiles
{
    public static bool IsPortableSegment(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try { return string.Equals(LibraryPaths.Segment(value), value, StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    public static bool IsValidSubfolder(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 1024 &&
        NormalizeSubfolder(value).Split('/').All(IsPortableSegment);

    public static bool AreValid(IReadOnlyList<FolderProfileOptions>? profiles)
    {
        if (profiles is null || profiles.Count > 100) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var profile in profiles)
            {
                if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 100 ||
                    profile.Name != profile.Name.Trim() || profile.Name.Any(char.IsControl) || profile.Name.Normalize() != profile.Name ||
                    !names.Add(profile.Name.Normalize()) || !IsValidSubfolder(profile.IngestSubfolder) ||
                    !folders.Add(NormalizeSubfolder(profile.IngestSubfolder)) ||
                    (profile.Collection is not null && !IsPortableSegment(profile.Collection)) ||
                    !Enum.IsDefined(profile.FilenameOrder)) return false;
                if (profile.Collection is not null)
                {
                    if (collections.TryGetValue(profile.Collection, out var prior) && prior != profile.Collection) return false;
                    collections[profile.Collection] = profile.Collection;
                }
            }
        }
        catch (ArgumentException) { return false; }
        return true;
    }

    public static FolderProfileSnapshot? Select(IReadOnlyList<FolderProfileOptions> profiles, string ingestRoot, string source)
    {
        if (!AreValid(profiles)) throw new IOException("Folder profiles are invalid. Check the configured folder rules.");
        LibraryPaths.EnsureSafe(ingestRoot, source);
        var parent = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var relative = Path.GetRelativePath(ingestRoot, parent).Replace(Path.DirectorySeparatorChar, '/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var profile = profiles.OrderByDescending(p => NormalizeSubfolder(p.IngestSubfolder).Length)
            .FirstOrDefault(p => relative.Equals(NormalizeSubfolder(p.IngestSubfolder), comparison) ||
                relative.StartsWith(NormalizeSubfolder(p.IngestSubfolder) + "/", comparison));
        return profile is null ? null : new FolderProfileSnapshot(profile.Name, NormalizeSubfolder(profile.IngestSubfolder),
            profile.Collection, profile.FilenameOrder);
    }

    public static string NormalizeSubfolder(string value) => value.Replace('\\', '/');
}
