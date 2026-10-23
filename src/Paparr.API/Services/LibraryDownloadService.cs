using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;

namespace Paparr.API.Services;

public sealed record LibraryDownload(Stream Stream, string FileName, string ContentType);

public sealed class LibraryDownloadConflictException(string message) : Exception(message);

public sealed class LibraryDownloadService(AppDbContext db, IOptions<IngestionOptions> options)
{
    public async Task<LibraryDownload?> OpenVerifiedAsync(long id, CancellationToken cancellationToken)
    {
        var book = await db.Books.AsNoTracking().Include(item => item.ImportJob)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null) return null;

        var path = book.FilePath;
        var expectedHash = book.ImportJob?.FileHash;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw Conflict();

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var (contentType, extensionForName) = extension switch
        {
            ".epub" => ("application/epub+zip", ".epub"),
            ".pdf" => ("application/pdf", ".pdf"),
            _ => throw Conflict()
        };

        FileStream? stream = null;
        try
        {
            LibraryPaths.EnsureSafe(options.Value.LibraryPath, path);
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var maxSize = options.Value.MaxFileSizeMb * 1024L * 1024;
            if (stream.Length > maxSize) throw Conflict();

            var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedHash)))
                throw Conflict();

            stream.Position = 0;
            var safeName = $"{LibraryPaths.Segment(book.Title)} - {LibraryPaths.Segment(book.Author)}{extensionForName}";
            var result = new LibraryDownload(stream, safeName, contentType);
            stream = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LibraryDownloadConflictException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Conflict();
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
        }
    }

    private static LibraryDownloadConflictException Conflict() =>
        new("The stored book file is unavailable or has changed. Run a library integrity scan and check the configured library permissions.");
}
