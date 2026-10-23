using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class EditionMetadataTests
{
    [Fact]
    public void Publisher_with_null_character_is_omitted_before_database_persistence() =>
        Assert.Null(EditionMetadataNormalizer.Create([], "Bad\0 press", null, null).Publisher);

    [Theory]
    [InlineData("0-306-40615-2", "9780306406157")]
    [InlineData("978-0-306-40615-7", "9780306406157")]
    [InlineData("urn:isbn:0-306-40615-2", "9780306406157")]
    [InlineData("9780306406158", null)]
    [InlineData("not an isbn", null)]
    public void ISBN_normalization_validates_checksums_and_converts_ISBN10(string input, string? expected) =>
        Assert.Equal(expected, EditionMetadataNormalizer.NormalizeIsbn(input));

    [Fact]
    public void ISBN_presence_status_distinguishes_absent_invalid_and_ambiguous_values()
    {
        Assert.Equal(EditionIsbnStatus.NotProvided, EditionMetadataNormalizer.Create([], null, null, null).IsbnStatus);
        Assert.Equal(EditionIsbnStatus.Invalid, EditionMetadataNormalizer.Create(["123"], null, null, null).IsbnStatus);
        var ambiguous = EditionMetadataNormalizer.Create(["9780306406157", "9780140328721"], null, null, null);
        Assert.Equal(EditionIsbnStatus.Ambiguous, ambiguous.IsbnStatus);
        Assert.Null(ambiguous.Isbn13);
    }

    [Fact]
    public void Edition_fields_are_bounded_and_dates_are_not_guessed()
    {
        var metadata = EditionMetadataNormalizer.Create([], new string('p', 301), "Spring 2020", "language-with-a-value-that-is-far-too-long-to-be-a-tag");
        Assert.Null(metadata.Publisher);
        Assert.Null(metadata.PublicationYear);
        Assert.Null(metadata.Language);
        Assert.Equal(2020, EditionMetadataNormalizer.Create([], null, "2020-04", "en-US").PublicationYear);
        Assert.Equal("en-us", EditionMetadataNormalizer.Create([], " Press ", "2020", "en-US").Language);
    }
}
