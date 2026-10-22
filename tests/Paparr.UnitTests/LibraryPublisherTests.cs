using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class LibraryPublisherTests
{
    [Fact]
    public async Task A_staging_collision_is_never_adopted_or_deleted()
    {
        using var library = new TemporaryLibrary();
        var source = library.Write("incoming");
        var hash = await new FileHashService().ComputeHashAsync(source);
        var destination = Path.Combine(library.Library, "book.epub");
        var stage = destination + ".collision.partial";
        await File.WriteAllTextAsync(stage, "unrelated bytes");
        var claimed = false;

        await Assert.ThrowsAsync<IOException>(() => library.Publisher().PrepareAsync(source, destination, stage, hash,
            _ => { claimed = true; return Task.CompletedTask; }, default));

        Assert.False(claimed);
        Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(stage));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Ownership_persistence_failure_removes_only_the_locally_created_empty_stage()
    {
        using var library = new TemporaryLibrary();
        var source = library.Write("incoming");
        var hash = await new FileHashService().ComputeHashAsync(source);
        var destination = Path.Combine(library.Library, "book.epub");
        var stage = destination + ".planned.partial";
        var owned = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => library.Publisher().PrepareAsync(source, destination,
            stage, hash, _ =>
            {
                owned = true;
                Assert.Equal(0, new FileInfo(stage).Length);
                Assert.Throws<IOException>(() => File.Open(stage, FileMode.Open, FileAccess.Write, FileShare.None));
                throw new InvalidOperationException("Ownership persistence failed.");
            }, default));

        Assert.True(owned);
        Assert.False(File.Exists(stage));
        Assert.False(File.Exists(destination));
        Assert.Equal("incoming", await File.ReadAllTextAsync(source));
    }

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
    public async Task Cleanup_does_not_treat_a_replacement_directory_as_a_removed_source()
    {
        using var library = new TemporaryLibrary();
        library.Settings.DeleteSourceAfterImport = true;
        var source = library.Write("original");
        var hash = await new FileHashService().ComputeHashAsync(source);
        var destination = Path.Combine(library.Library, "book.epub");
        await library.Publisher().PublishAsync(source, destination, hash, default);
        File.Delete(source);
        Directory.CreateDirectory(source);
        var replacement = Path.Combine(source, "keep.txt");
        await File.WriteAllTextAsync(replacement, "unrelated content");

        var error = await Record.ExceptionAsync(() => library.Publisher()
            .CleanupSourceAsync(source, destination, hash, default));

        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("unrelated content", await File.ReadAllTextAsync(replacement));
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
