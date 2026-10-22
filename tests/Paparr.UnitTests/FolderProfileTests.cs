using Paparr.API.Domain;
using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class FolderProfileTests
{
    [Fact]
    public void Longest_directory_prefix_wins_without_matching_a_sibling_name()
    {
        using var library = new TemporaryLibrary();
        FolderProfileOptions[] profiles = [Profile("Parent", "Research", "General"), Profile("Papers", "Research/Papers", "Science")];
        var nested = FolderProfiles.Select(profiles, library.Ingest, Path.Combine(library.Ingest, "Research", "Papers", "Drafts", "book.pdf"));
        Assert.Equal("Papers", nested?.Name);
        Assert.Equal("Parent", FolderProfiles.Select(profiles, library.Ingest, Path.Combine(library.Ingest, "Research", "Other", "book.pdf"))?.Name);
        Assert.Null(FolderProfiles.Select(profiles, library.Ingest, Path.Combine(library.Ingest, "Research2", "book.pdf")));
        Assert.Null(FolderProfiles.Select(profiles, library.Ingest, Path.Combine(library.Ingest, "book.pdf")));
        var caseMatch = FolderProfiles.Select(profiles, library.Ingest, Path.Combine(library.Ingest, "research", "book.pdf"));
        Assert.Equal(OperatingSystem.IsWindows() ? "Parent" : null, caseMatch?.Name);
    }

    [Fact]
    public void Configuration_separators_are_normalized_and_filename_only_profiles_are_valid()
    {
        using var library = new TemporaryLibrary();
        var profile = Profile("Reading", "Research\\Papers", null);
        library.Settings.FolderProfiles.Add(profile);
        Assert.True(library.Settings.HasValidFolderProfiles());
        var selected = FolderProfiles.Select([profile], library.Ingest, Path.Combine(library.Ingest, "Research", "Papers", "book.pdf"));
        Assert.Equal("Research/Papers", selected?.IngestSubfolder);
        Assert.Null(selected?.Collection);
        Assert.False(Directory.Exists(Path.Combine(library.Ingest, "Research")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/root")]
    [InlineData("C:relative")]
    [InlineData("C:\\root")]
    [InlineData("\\\\server\\share")]
    [InlineData("research//papers")]
    [InlineData("research/./papers")]
    [InlineData("research/../papers")]
    [InlineData("research/CON")]
    [InlineData("research/control\u0001")]
    [InlineData("research/trailing.")]
    public void Unsafe_or_ambiguous_source_prefixes_are_rejected(string folder) =>
        Assert.False(FolderProfiles.AreValid([Profile("Reading", folder, "Books")]));

    [Theory]
    [InlineData("../escape")]
    [InlineData("/root")]
    [InlineData("C:root")]
    [InlineData("Books/More")]
    [InlineData("Books\\More")]
    [InlineData("NUL.txt")]
    [InlineData("Books ")]
    [InlineData("Books\u0000")]
    [InlineData("")]
    public void Collection_names_are_exact_portable_segments_and_are_rechecked_for_publication(string collection)
    {
        using var library = new TemporaryLibrary();
        Assert.False(FolderProfiles.AreValid([Profile("Reading", "research", collection)]));
        Assert.Throws<IOException>(() => LibraryPaths.Destination(library.Library, "Book", "Author", ".pdf", new string('A', 64), collection));
        Assert.Empty(Directory.GetFileSystemEntries(library.Library));
    }

    [Fact]
    public void Case_aliases_invalid_unicode_and_unknown_orders_are_rejected_without_rewriting_configuration()
    {
        Assert.False(FolderProfiles.AreValid([Profile("Reading", "research", "Books"), Profile("reading", "other", "Other")]));
        Assert.False(FolderProfiles.AreValid([Profile("Reading", "research", "Books"), Profile("Other", "RESEARCH", "Other")]));
        Assert.False(FolderProfiles.AreValid([Profile("Reading", "research", "Books"), Profile("Other", "other", "books")]));
        foreach (var invalid in new[] { "\ud800", "e\u0301" })
        {
            Assert.False(FolderProfiles.AreValid([Profile("Reading", "research", invalid)]));
            Assert.False(FolderProfiles.AreValid([Profile(invalid, "research", "Books")]));
            Assert.False(FolderProfiles.AreValid([Profile("Reading", invalid, "Books")]));
        }
        var profile = Profile("Reading", "research", "Books");
        profile.FilenameOrder = (FilenameOrder)42;
        Assert.False(FolderProfiles.AreValid([profile]));
        Assert.True(FolderProfiles.AreValid([Profile("Reading", "research", "Books"), Profile("Other", "other", "Books")]));
    }

    [Fact]
    public void Each_filename_order_preserves_title_subtitles_and_default_parsing_remains_unchanged()
    {
        var metadata = new MetadataService();
        Assert.Equal(("Title - Subtitle", "Author"), metadata.ParseFilename("Author - Title - Subtitle.pdf", FilenameOrder.AuthorTitle));
        Assert.Equal(("Title - Subtitle", "Author"), metadata.ParseFilename("Title - Subtitle - Author.pdf"));
        Assert.Null(metadata.ParseFilename("Author - .pdf", FilenameOrder.AuthorTitle));
        Assert.Null(metadata.ParseFilename(" - Title.pdf", FilenameOrder.AuthorTitle));
    }

    [Fact]
    public void Collection_routing_preserves_the_existing_author_and_title_layout()
    {
        using var library = new TemporaryLibrary();
        var standard = LibraryPaths.Destination(library.Library, "Title", "Author", ".pdf", new string('A', 64));
        var collection = LibraryPaths.Destination(library.Library, "Title", "Author", ".pdf", new string('A', 64), "Reading");
        Assert.Equal(Path.Combine("Reading", Path.GetRelativePath(library.Library, standard)), Path.GetRelativePath(library.Library, collection));
        Assert.Throws<IOException>(() => LibraryPaths.EnsureSafeDestination(library.Library, standard, "Reading"));
    }

    private static FolderProfileOptions Profile(string name, string folder, string? collection) =>
        new() { Name = name, IngestSubfolder = folder, Collection = collection, FilenameOrder = FilenameOrder.AuthorTitle };
}
