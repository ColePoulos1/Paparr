using System.Security.Cryptography;
using System.Text;

namespace Paparr.API.Domain;

public static class SourcePathIdentity
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string Canonicalize(string path) => Path.GetFullPath(path);

    public static bool SamePath(string first, string second) =>
        string.Equals(Canonicalize(first), Canonicalize(second), PathComparison);

    public static string CreateKey(string path)
    {
        var canonical = Canonicalize(path);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
