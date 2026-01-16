using Paparr.API.Data;
using Paparr.API.Domain;
using Microsoft.EntityFrameworkCore;

namespace Paparr.API.Services;

public class EbookIngestionService : IEbookIngestionService
{
    private readonly AppDbContext _db;
    private readonly IMetadataService _metadataService;
    private readonly IMetadataEnricherService _enricherService;
    private readonly ILogger<EbookIngestionService> _logger;
    private readonly string _libraryPath;
    private readonly string _ingestPath;

    public EbookIngestionService(
        AppDbContext db,
        IMetadataService metadataService,
        IMetadataEnricherService enricherService,
        ILogger<EbookIngestionService> logger,
        IConfiguration config)
    {
        _db = db;
        _metadataService = metadataService;
        _enricherService = enricherService;
        _logger = logger;
        _libraryPath = config["LibraryPath"] ?? "/library";
        _ingestPath = config["IngestPath"] ?? "/ingest";
    }

    public async Task ProcessImportJobAsync(ImportJob job)
    {
        try
        {
            job.Status = ImportStatus.Processing;
            job.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            if (!File.Exists(job.FilePath))
            {
                _logger.LogError("File not found: {FilePath}", job.FilePath);
                job.Status = ImportStatus.Failed;
                job.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return;
            }

            var ext = Path.GetExtension(job.FilePath).ToLower();
            
            // Extract metadata from file
            (string Title, string Author)? fileMetadata = null;
            if (ext == ".epub")
            {
                fileMetadata = await _metadataService.ExtractEpubMetadataAsync(job.FilePath);
            }
            else if (ext == ".pdf")
            {
                fileMetadata = await _metadataService.ExtractPdfMetadataAsync(job.FilePath);
            }

            // Fallback to filename parsing
            fileMetadata ??= _metadataService.ParseFilename(Path.GetFileName(job.FilePath));

            if (fileMetadata == null)
            {
                _logger.LogWarning("Could not extract metadata for {FilePath}", job.FilePath);
                job.Status = ImportStatus.Failed;
                job.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return;
            }

            var (title, author) = fileMetadata.Value;

            // Add embedded metadata as candidate
            var candidates = new List<MetadataCandidate>
            {
                new MetadataCandidate
                {
                    Title = title,
                    Author = author,
                    Source = "embedded",
                    ExternalId = "",
                    ConfidenceScore = 85, // High confidence for embedded/parsed metadata
                    ImportJobId = job.Id,
                    CreatedAt = DateTime.UtcNow
                }
            };

            // Query external APIs
            var olCandidates = await _enricherService.QueryOpenLibraryAsync(title, author, job.Id);
            candidates.AddRange(olCandidates);

            var gbCandidates = await _enricherService.QueryGoogleBooksAsync(title, author, job.Id);
            candidates.AddRange(gbCandidates);

            // Save candidates
            _db.MetadataCandidates.AddRange(candidates);
            await _db.SaveChangesAsync();

            // Auto-accept if confidence is high
            var bestCandidate = candidates.OrderByDescending(c => c.ConfidenceScore).FirstOrDefault();
            if (bestCandidate?.ConfidenceScore >= 90)
            {
                _logger.LogInformation("Auto-accepting high-confidence match: {Title} by {Author} (score: {Score})",
                    bestCandidate.Title, bestCandidate.Author, bestCandidate.ConfidenceScore);
                await AcceptCandidateAsync(job, bestCandidate);
            }
            else
            {
                job.Status = ImportStatus.AwaitingApproval;
                job.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Import job {JobId} awaiting approval. Best match: {Title} (score: {Score})",
                    job.Id, bestCandidate?.Title ?? "Unknown", bestCandidate?.ConfidenceScore ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing import job {JobId}", job.Id);
            job.Status = ImportStatus.Failed;
            job.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task AcceptCandidateAsync(ImportJob job, MetadataCandidate candidate)
    {
        try
        {
            var ext = Path.GetExtension(job.FilePath);
            
            // Calibre-compatible structure: /library/Author Name/Book Title/file.ext
            var authorFolder = Path.Combine(_libraryPath, SanitizePath(candidate.Author));
            var bookFolder = Path.Combine(authorFolder, SanitizePath(candidate.Title));
            
            Directory.CreateDirectory(bookFolder);

            var destinationPath = Path.Combine(bookFolder, $"{SanitizePath(candidate.Title)}{ext}");
            
            // Move file to library
            if (File.Exists(destinationPath))
            {
                _logger.LogWarning("File already exists at {Path}, overwriting", destinationPath);
            }

            File.Copy(job.FilePath, destinationPath, overwrite: true);

            // Create Book record
            var book = new Book
            {
                Title = candidate.Title,
                Author = candidate.Author,
                Source = candidate.Source,
                ExternalId = candidate.ExternalId,
                FilePath = destinationPath,
                ImportedAt = DateTime.UtcNow,
                ImportJobId = job.Id
            };

            _db.Books.Add(book);

            // Update job status
            job.Status = ImportStatus.Completed;
            job.UpdatedAt = DateTime.UtcNow;

            // Delete original file
            try
            {
                File.Delete(job.FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete original file {FilePath}", job.FilePath);
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation("Import job {JobId} completed. Book: {Title} by {Author}", 
                job.Id, book.Title, book.Author);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting candidate for import job {JobId}", job.Id);
            job.Status = ImportStatus.Failed;
            job.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    private string SanitizePath(string path)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(path.Where(c => !invalidChars.Contains(c)).ToArray())
            .Replace(" ", "_")
            .Trim('_');
    }
}
