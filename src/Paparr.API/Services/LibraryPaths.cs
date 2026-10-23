using System.Text;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public static class LibraryPaths
{
    private static readonly HashSet<char> Forbidden = new("<>:\"/\\|?*");
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Segment(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC);
        var result = new string(normalized.Select(c => Forbidden.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        result = result.Trim().TrimEnd('.');
        var bounded = new StringBuilder();
        var bytes = 0;
        foreach (var rune in result.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 70) break;
            bounded.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        result = bounded.ToString().TrimEnd('.', ' ');
        if (result.Length == 0 || result is "." or "..") result = "Unknown";
        if (Reserved.Contains(result.Split('.')[0])) result = "_" + result;
        return result;
    }

    public static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, comparison) || path.StartsWith(prefix, comparison);
    }

    public static void EnsureSafe(string root, string path)
    {
        if (!IsWithin(root, path)) throw new IOException("The file is outside the configured directory.");
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Symbolic links are not supported in import or library paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string Destination(string root, string title, string author, string extension, string hash, string? collection = null)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid SHA-256 hash.", nameof(hash));
        extension = extension.ToLowerInvariant();
        if (extension is not ".epub" and not ".pdf") throw new ArgumentException("Unsupported ebook format.", nameof(extension));
        var collectionRoot = CollectionRoot(root, collection);
        var path = Path.GetFullPath(Path.Combine(collectionRoot, Segment(author), Segment(title),
            $"{Segment(title)} - {Segment(author)} [{hash[..12].ToLowerInvariant()}]{extension}"));
        EnsureSafeDestination(root, path, collection);
        return path;
    }

    public static string ImportDestination(string root, ImportJob job, MetadataCandidate candidate)
    {
        try
        {
            return Destination(root, candidate.Title, candidate.Author, Path.GetExtension(job.FilePath), job.FileHash, job.CollectionSegment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ImportConflictException("The saved collection or library path is unsafe. Check the folder profile and library directory before importing.");
        }
    }

    public static void EnsureSafeDestination(string root, string destination, string? collection)
    {
        var collectionRoot = CollectionRoot(root, collection);
        EnsureSafe(root, collectionRoot);
        EnsureSafe(root, destination);
        EnsureSafe(collectionRoot, destination);
    }

    private static string CollectionRoot(string root, string? collection)
    {
        if (collection is null) return root;
        if (!FolderProfiles.IsPortableSegment(collection))
            throw new IOException("The saved collection name is invalid. Check the folder profile before importing.");
        return Path.Combine(root, collection);
    }
}
