using Microsoft.Extensions.Options;

namespace Paparr.API.Services;

public interface ILibraryPublisher
{
    Task PublishAsync(string source, string destination, string hash, CancellationToken cancellationToken);
    Task<bool> PrepareAsync(string source, string destination, string staging, string hash,
        Func<CancellationToken, Task> markOwned, CancellationToken cancellationToken);
    Task PromoteAsync(string staging, string destination, string hash, CancellationToken cancellationToken);
    bool StageExists(string staging);
    void DeleteStage(string staging);
    Task CleanupSourceAsync(string source, string destination, string hash, CancellationToken cancellationToken);
}

public sealed class LibraryPublisher(IFileHashService hashes, IOptions<IngestionOptions> options) : ILibraryPublisher
{
    public async Task PublishAsync(string source, string destination, string hash, CancellationToken cancellationToken)
    {
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var owned = false;
        try
        {
            await PrepareAsync(source, destination, staging, hash, _ =>
            {
                owned = true;
                return Task.CompletedTask;
            }, cancellationToken);
            await PromoteAsync(staging, destination, hash, cancellationToken);
        }
        finally
        {
            if (owned) DeleteStage(staging);
        }
    }

    public async Task<bool> PrepareAsync(string source, string destination, string staging, string hash,
        Func<CancellationToken, Task> markOwned, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, destination);
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, staging);
        if (File.Exists(destination))
        {
            await VerifyAsync(destination, hash, cancellationToken);
            return false;
        }

        LibraryPaths.EnsureSafe(options.Value.IngestPath, source);
        var info = new FileInfo(source);
        if (!info.Exists) throw new IOException("The source file is missing. Restore it before retrying.");
        if (info.Length > options.Value.MaxFileSizeMb * 1024L * 1024)
            throw new IOException("The source file exceeds the configured size limit.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, destination);
        var created = false;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                created = true;
                // Confirm ownership durably while the exclusive creation handle remains open.
                await markOwned(cancellationToken);
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            await VerifyAsync(staging, hash, cancellationToken);
            return true;
        }
        catch
        {
            if (created) DeleteStage(staging);
            throw;
        }
    }

    public async Task PromoteAsync(string staging, string destination, string hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, staging);
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, destination);
        if (File.Exists(destination))
        {
            await VerifyAsync(destination, hash, cancellationToken);
            return;
        }
        // PrepareAsync already verified these owned bytes before the publication lock.
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, staging);
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, destination);
        try { File.Move(staging, destination, overwrite: false); }
        catch (IOException) when (File.Exists(destination))
        {
            LibraryPaths.EnsureSafe(options.Value.LibraryPath, destination);
            await VerifyAsync(destination, hash, cancellationToken);
        }
    }

    public bool StageExists(string staging)
    {
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, staging);
        try
        {
            if (File.GetAttributes(staging).HasFlag(FileAttributes.Directory))
                throw new IOException("The staging path is a directory. No cleanup was performed.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public void DeleteStage(string staging)
    {
        if (StageExists(staging)) File.Delete(staging);
    }

    public async Task CleanupSourceAsync(string source, string destination, string hash, CancellationToken cancellationToken)
    {
        if (!options.Value.DeleteSourceAfterImport) return;
        LibraryPaths.EnsureSafe(options.Value.IngestPath, source);
        // Exists also returns false on access errors; only confirmed absence completes cleanup.
        try
        {
            if (File.GetAttributes(source).HasFlag(FileAttributes.Directory))
                throw new IOException("The source path is a directory. No cleanup was performed.");
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        LibraryPaths.EnsureSafe(options.Value.LibraryPath, destination);
        await VerifyAsync(destination, hash, cancellationToken);
        await VerifyAsync(source, hash, cancellationToken);
        File.Delete(source);
    }

    private async Task VerifyAsync(string path, string hash, CancellationToken cancellationToken)
    {
        if (!string.Equals(await hashes.ComputeHashAsync(path, cancellationToken), hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("File content changed or the destination contains a different book. No file was replaced.");
    }
}
