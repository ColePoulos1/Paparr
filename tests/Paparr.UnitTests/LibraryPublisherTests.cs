using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class LibraryPublisherTests
{
    [Fact]
    public async Task Publication_is_verified_repeatable_and_retains_the_source()
    {
        using var library = new TemporaryLibrary();
        var source = library.Write("original content");
        var hash = await new FileHashService().ComputeHashAsync(source);
        var destination = LibraryPaths.Destination(library.Library, "Book", "Author", ".epub", hash);
        await library.Publisher().PublishAsync(source, destination, hash, default);
        await library.Publisher().PublishAsync(source, destination, hash, default);
        await library.Publisher().CleanupSourceAsync(source, destination, hash, default);
        Assert.Equal("original content", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(source));
        Assert.Single(Directory.GetFiles(library.Library, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Existing_different_content_is_never_overwritten()
    {
        using var library = new TemporaryLibrary();
        var source = library.Write("incoming");
        var hash = await new FileHashService().ComputeHashAsync(source);
        var destination = Path.Combine(library.Library, "occupied.epub");
        await File.WriteAllTextAsync(destination, "existing");
        await Assert.ThrowsAsync<IOException>(() => library.Publisher().PublishAsync(source, destination, hash, default));
        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_changed_source_does_not_publish_or_leave_staging_files()
    {
        using var library = new TemporaryLibrary();
        var source = library.Write("before");
        var hash = await new FileHashService().ComputeHashAsync(source);
        await File.WriteAllTextAsync(source, "after");
        await Assert.ThrowsAsync<IOException>(() => library.Publisher().PublishAsync(source, Path.Combine(library.Library, "book.epub"), hash, default));
        Assert.Empty(Directory.GetFiles(library.Library, "*", SearchOption.AllDirectories));
        Assert.Equal("after", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Cleanup_requires_a_verified_destination_even_when_enabled()
    {
        using var library = new TemporaryLibrary();
        library.Settings.DeleteSourceAfterImport = true;
        var source = library.Write("original");
        var hash = await new FileHashService().ComputeHashAsync(source);
        var destination = Path.Combine(library.Library, "book.epub");
        await File.WriteAllTextAsync(destination, "damaged");
        await Assert.ThrowsAsync<IOException>(() => library.Publisher().CleanupSourceAsync(source, destination, hash, default));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Cleanup_removes_only_an_unchanged_source_after_verified_publication()
    {
        using var library = new TemporaryLibrary();
        library.Settings.DeleteSourceAfterImport = true;
        var source = library.Write("original");
        var hash = await new FileHashService().ComputeHashAsync(source);
        var destination = Path.Combine(library.Library, "book.epub");
        await library.Publisher().PublishAsync(source, destination, hash, default);
        await library.Publisher().CleanupSourceAsync(source, destination, hash, default);
        Assert.False(File.Exists(source));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Cancelled_publication_leaves_no_final_file()
    {
        using var library = new TemporaryLibrary();
        var source = library.Write("original");
        var hash = await new FileHashService().ComputeHashAsync(source);
        var destination = Path.Combine(library.Library, "book.epub");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.Publisher().PublishAsync(source, destination, hash, cancellation.Token));
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(library.Library, "*", SearchOption.AllDirectories));
    }
}
