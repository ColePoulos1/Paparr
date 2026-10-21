using System.Text;

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

    public static string Destination(string root, string title, string author, string extension, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid SHA-256 hash.", nameof(hash));
        extension = extension.ToLowerInvariant();
        if (extension is not ".epub" and not ".pdf") throw new ArgumentException("Unsupported ebook format.", nameof(extension));
        var path = Path.GetFullPath(Path.Combine(root, Segment(author), Segment(title),
            $"{Segment(title)} - {Segment(author)} [{hash[..12].ToLowerInvariant()}]{extension}"));
        EnsureSafe(root, path);
        return path;
    }
}
