using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class LibraryPathsTests
{
    [Fact]
    public void Unicode_segments_fit_portable_byte_limits_without_splitting_characters()
    {
        var result = LibraryPaths.Segment(string.Concat(Enumerable.Repeat("📚漢字", 100)));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= 70);
        Assert.DoesNotContain("\uFFFD", result);
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("/root/book")]
    [InlineData("C:\\Windows\\system32")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("LPT1")]
    [InlineData("trailing. ")]
    [InlineData("A/B:C*D?")]
    [InlineData("Émile Zola")]
    public void Metadata_always_produces_a_contained_path(string metadata)
    {
        using var library = new TemporaryLibrary();
        var path = LibraryPaths.Destination(library.Library, metadata, metadata, ".EPUB", new string('A', 64));
        Assert.True(LibraryPaths.IsWithin(library.Library, path));
        Assert.EndsWith(".epub", path);
        Assert.DoesNotContain("..", Path.GetRelativePath(library.Library, path).Split(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_sibling_with_the_same_prefix_is_not_within_the_root()
    {
        using var library = new TemporaryLibrary();
        Assert.False(LibraryPaths.IsWithin(library.Library, library.Library + "-other/book.epub"));
        Assert.Throws<IOException>(() => LibraryPaths.EnsureSafe(library.Library, Path.Combine(library.Root, "outside")));
    }

    [Fact]
    public void Different_content_gets_a_different_destination()
    {
        using var library = new TemporaryLibrary();
        Assert.NotEqual(
            LibraryPaths.Destination(library.Library, "Title", "Author", ".pdf", new string('A', 64)),
            LibraryPaths.Destination(library.Library, "Title", "Author", ".pdf", new string('B', 64)));
    }

    [Fact]
    public void Overlapping_ingest_and_library_roots_are_rejected()
    {
        using var library = new TemporaryLibrary();
        library.Settings.LibraryPath = Path.Combine(library.Ingest, "books");
        Assert.False(library.Settings.HasSeparateRoots());
    }

    [Theory]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("  Title. ", "Title")]
    [InlineData("..", "Unknown")]
    public void Segments_are_portable(string input, string expected) => Assert.Equal(expected, LibraryPaths.Segment(input));
}
