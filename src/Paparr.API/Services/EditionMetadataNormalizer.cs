using System.Globalization;
using System.Text.RegularExpressions;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed record NormalizedEditionMetadata(string? Isbn13, EditionIsbnStatus IsbnStatus,
    string? Publisher, int? PublicationYear, string? Language);

public static partial class EditionMetadataNormalizer
{
    public static NormalizedEditionMetadata Create(IEnumerable<string?>? isbnValues, string? publisher,
        string? publicationDate, string? language)
    {
        var provided = isbnValues?.Select(value => (Raw: value, Normalized: NormalizeIsbn(value)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Raw)).ToList() ?? [];
        var validIsbns = provided.Where(item => item.Normalized is not null)
            .Select(item => item.Normalized!).Distinct(StringComparer.Ordinal).ToArray();
        var (isbn, status) = validIsbns.Length switch
        {
            1 => (validIsbns[0], EditionIsbnStatus.Valid),
            > 1 => ((string?)null, EditionIsbnStatus.Ambiguous),
            _ => (null, provided.Count > 0 ? EditionIsbnStatus.Invalid : EditionIsbnStatus.NotProvided)
        };

        return new NormalizedEditionMetadata(isbn, status, BoundedPublisher(publisher),
            ParsePublicationYear(publicationDate), NormalizeLanguage(language));
    }

    public static string? NormalizeIsbn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return null;
        value = value.Trim();
        const string urnPrefix = "urn:isbn:";
        if (value.StartsWith(urnPrefix, StringComparison.OrdinalIgnoreCase)) value = value[urnPrefix.Length..];
        var compact = new string(value.Where(character => character != '-' && !char.IsWhiteSpace(character)).ToArray());
        if (compact.Length == 10 && compact.Take(9).All(IsAsciiDigit) &&
            (compact[9] == 'X' || compact[9] == 'x' || IsAsciiDigit(compact[9])))
        {
            var sum = 0;
            for (var index = 0; index < 10; index++)
            {
                var digit = index == 9 && (compact[index] is 'X' or 'x') ? 10 : compact[index] - '0';
                sum += (10 - index) * digit;
            }
            if (sum % 11 != 0) return null;
            var firstTwelve = "978" + compact[..9];
            return firstTwelve + CheckDigit(firstTwelve);
        }

        if (compact.Length != 13 || !compact.All(IsAsciiDigit) ||
            !(compact.StartsWith("978", StringComparison.Ordinal) || compact.StartsWith("979", StringComparison.Ordinal)))
            return null;
        var expected = CheckDigit(compact[..12]);
        return compact[12] - '0' == expected ? compact : null;
    }

    public static NormalizedEditionMetadata CreateManual(string? isbn, string? publisher, int? publicationYear,
        string? language)
    {
        var result = Create([isbn], publisher, publicationYear?.ToString(CultureInfo.InvariantCulture), language);
        return result with { PublicationYear = publicationYear is >= 1000 and <= 9999 ? publicationYear : null };
    }

    private static int CheckDigit(string firstTwelve)
    {
        var sum = firstTwelve.Select((character, index) => (character - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        return (10 - sum % 10) % 10;
    }

    private static int? ParsePublicationYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return null;
        var text = value.Trim();
        if (Regex.IsMatch(text, @"^\d{4}$", RegexOptions.CultureInvariant))
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year >= 1000 ? year : null;
        if (DateOnly.TryParseExact(text, ["yyyy-MM", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return date.Year;
        if (text.Length >= 20 && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ssK",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)) return timestamp.Year;
        return null;
    }

    private static string? BoundedPublisher(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) return null;
        var normalized = value.Trim();
        return normalized.Length is > 0 and <= 300 ? normalized : null;
    }

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 35) return null;
        var normalized = value.Trim();
        if (!LanguageTagRegex().IsMatch(normalized)) return null;
        return normalized.ToLowerInvariant();
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    [GeneratedRegex("^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagRegex();
}
