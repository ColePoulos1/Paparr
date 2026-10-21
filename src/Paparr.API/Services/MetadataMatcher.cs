using System.Globalization;
using System.Text;

namespace Paparr.API.Services;

public static class MetadataMatcher
{
    public static string Normalize(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) result.Append(char.ToLowerInvariant(c));
            else if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
        }
        return result.ToString().Trim();
    }

    public static (decimal Score, string Evidence) Compare(string title, string author, string candidateTitle, string candidateAuthor)
    {
        var titleScore = Similarity(title, candidateTitle);
        var authorScore = Similarity(author, candidateAuthor);
        return (Math.Round(60 * titleScore + 40 * authorScore, 1),
            FormattableString.Invariant($"Title match {titleScore * 100:0}/100; author match {authorScore * 100:0}/100."));
    }

    private static decimal Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Length > 1000 || b.Length > 1000) return 0;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (current, previous) = (previous, current);
        }
        return 1 - previous[b.Length] / (decimal)Math.Max(a.Length, b.Length);
    }
}
