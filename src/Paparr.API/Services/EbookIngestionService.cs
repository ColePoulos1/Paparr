using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;

namespace Paparr.API.Services;

public sealed class EbookIngestionService(AppDbContext db, IMetadataService metadata,
    IMetadataEnricherService providers, ILibraryPublisher publisher, IFileHashService hashes,
    IOptions<IngestionOptions> options, TimeProvider clock, ILogger<EbookIngestionService> logger,
    SourceCleanupService cleanup, StagingService staging) : IEbookIngestionService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task ProcessImportJobAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        if (job.Status != ImportStatus.Pending) return;
        if (job.AttemptCount >= options.Value.MaxAttempts)
        {
            await FailAsync(job, "The attempt limit was reached. Check the file and increase MaxAttempts to retry.", cancellationToken);
            return;
        }
        job.AttemptCount++;
        job.LeaseExpiresAt = Now.AddMinutes(options.Value.LeaseMinutes);
        job.LastError = null;
        job.Status = job.SelectedCandidateId.HasValue ? ImportStatus.Publishing : ImportStatus.Processing;
        if (job.Status == ImportStatus.Publishing) job.PublicationFence = Guid.NewGuid();
        Record(job, $"Attempt {job.AttemptCount} started.");
        await db.SaveChangesAsync(cancellationToken);

        if (job.SelectedCandidateId.HasValue)
        {
            await PublishAsync(job, cancellationToken);
            return;
        }

        try
        {
            LibraryPaths.EnsureSafe(options.Value.IngestPath, job.FilePath);
            if (new FileInfo(job.FilePath).Length > options.Value.MaxFileSizeMb * 1024L * 1024)
                throw new InvalidDataException("The file is too large.");
            if (await hashes.ComputeHashAsync(job.FilePath, cancellationToken) != job.FileHash)
            {
                await FailAsync(job, "The source changed after discovery. Restore the original file or import the changed copy.", cancellationToken);
                return;
            }
            EmbeddedEpubMetadata? epubMetadata = null;
            var extension = Path.GetExtension(job.FilePath).ToLowerInvariant();
            var embedded = extension switch
            {
                ".epub" => ToTitleAuthor(epubMetadata = await metadata.ExtractEpubMetadataAsync(job.FilePath, cancellationToken)),
                ".pdf" => await metadata.ExtractPdfMetadataAsync(job.FilePath, cancellationToken),
                _ => throw new InvalidDataException("Unsupported ebook format.")
            };
            var extracted = embedded ?? metadata.ParseFilename(Path.GetFileName(job.FilePath));
            db.MetadataCandidates.RemoveRange(job.Candidates);
            job.Candidates.Clear();
            if (extracted is { } book)
            {
                var edition = epubMetadata is null ? null : EditionMetadataNormalizer.Create(epubMetadata.Isbns,
                    epubMetadata.Publisher, epubMetadata.PublicationDate, epubMetadata.Language);
                job.Candidates.Add(new MetadataCandidate
                {
                    Title = book.Title,
                    Author = book.Author,
                    Source = embedded.HasValue ? "embedded" : "filename",
                    ExternalId = "",
                    ConfidenceScore = embedded.HasValue ? 85 : 60,
                    Evidence = embedded.HasValue ? "Read from the ebook metadata." : "Parsed from the filename; review before importing.",
                    Isbn13 = edition?.Isbn13,
                    IsbnStatus = edition?.IsbnStatus ?? EditionIsbnStatus.NotProvided,
                    Publisher = edition?.Publisher,
                    PublicationYear = edition?.PublicationYear,
                    Language = edition?.Language,
                    CreatedAt = Now
                });
                var openLibrary = await providers.QueryOpenLibraryAsync(book.Title, book.Author, job.Id, cancellationToken);
                var googleBooks = await providers.QueryGoogleBooksAsync(book.Title, book.Author, job.Id, cancellationToken);
                foreach (var candidate in openLibrary.Concat(googleBooks)) job.Candidates.Add(candidate);
            }
            job.Status = ImportStatus.AwaitingApproval;
            job.LeaseExpiresAt = null;
            Record(job, extracted.HasValue ? "Metadata is ready for review." : "No complete metadata found. Enter a title and author to continue.");
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DbUpdateException)
        {
            logger.LogWarning(ex, "Extraction failed for import {ImportId}", job.Id);
            await FailAsync(job, "The ebook could not be read. Check that the file is complete, supported, and within the size limit.", cancellationToken);
        }
    }

    public async Task AcceptCandidateAsync(ImportJob job, MetadataCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (job.Status == ImportStatus.Completed && job.SelectedCandidateId == candidate.Id) return;
        if (job.Status != ImportStatus.AwaitingApproval)
            throw new ImportConflictException("This import is no longer awaiting review. Refresh its current state.");
        if (candidate.ImportJobId != job.Id)
            throw new ImportConflictException("The selected metadata does not belong to this import.");
        job.DestinationPath = LibraryPaths.Destination(options.Value.LibraryPath, candidate.Title, candidate.Author,
            Path.GetExtension(job.FilePath), job.FileHash);
        job.SelectedCandidateId = candidate.Id;
        job.Status = ImportStatus.Publishing;
        job.PublicationFence = Guid.NewGuid();
        job.LeaseExpiresAt = Now.AddMinutes(options.Value.LeaseMinutes);
        Record(job, $"Approved metadata from {candidate.Source}.");
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(job, cancellationToken);
    }

    public async Task RecoverAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        if (job.Status is not (ImportStatus.Processing or ImportStatus.Publishing) || job.LeaseExpiresAt > Now) return;
        job.Status = ImportStatus.Pending;
        job.LeaseExpiresAt = null;
        Record(job, "Recovered an interrupted attempt.");
        await db.SaveChangesAsync(cancellationToken);
        await ProcessImportJobAsync(job, cancellationToken);
    }

    private async Task PublishAsync(ImportJob job, CancellationToken cancellationToken)
    {
        var candidate = job.Candidates.Single(c => c.Id == job.SelectedCandidateId);
        var fence = job.PublicationFence!.Value;
        try
        {
            var stage = await staging.PrepareAsync(job, fence, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await OwnsPublicationAsync(job, fence, cancellationToken)) return;
            await publisher.PromoteAsync(stage.FilePath, job.DestinationPath!, job.FileHash, cancellationToken);
            job.AcceptedBook = new Book
            {
                Title = candidate.Title,
                Author = candidate.Author,
                Source = candidate.Source,
                ExternalId = candidate.ExternalId,
                Isbn13 = candidate.Isbn13,
                IsbnStatus = candidate.IsbnStatus,
                Publisher = candidate.Publisher,
                PublicationYear = candidate.PublicationYear,
                Language = candidate.Language,
                FilePath = job.DestinationPath,
                ImportedAt = Now,
                IntegrityStatus = IntegrityStatus.Verified,
                IntegrityCheckedAt = Now,
                ImportJobId = job.Id
            };
            job.Status = ImportStatus.Completed;
            job.LeaseExpiresAt = null;
            job.LastError = null;
            job.CleanupStatus = options.Value.DeleteSourceAfterImport ? SourceCleanupStatus.Pending : SourceCleanupStatus.NotRequested;
            job.CleanupAfter = options.Value.DeleteSourceAfterImport ? Now : null;
            stage.Status = PublicationStageStatus.Published;
            stage.UpdatedAt = Now;
            Record(job, "Verified library copy and completed the import.");
            // A failed commit leaves verified bytes available for a later fenced publication.
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await OwnsPublicationAsync(job, fence, cancellationToken)) return;
            logger.LogWarning(ex, "Publication failed for import {ImportId}", job.Id);
            await FailAsync(job, "The book could not be published. Check the source, destination, permissions, and available space, then retry.", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new ImportConflictException(job.LastError!);
        }
        try
        {
            await cleanup.TryCleanupAsync(job, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or DbUpdateException)
        {
            logger.LogWarning(ex, "Import {ImportId} completed; its source was retained", job.Id);
        }
    }

    private async Task<bool> OwnsPublicationAsync(ImportJob job, Guid fence, CancellationToken ct)
    {
        var current = await db.ImportJobs.FromSqlInterpolated($"SELECT * FROM \"ImportJobs\" WHERE \"Id\" = {job.Id} FOR UPDATE")
            .AsNoTracking().SingleAsync(ct);
        return current.Status == ImportStatus.Publishing && current.PublicationFence == fence && current.Revision == job.Revision;
    }

    private async Task FailAsync(ImportJob job, string message, CancellationToken ct)
    {
        job.Status = ImportStatus.Failed;
        job.LastError = message;
        job.LeaseExpiresAt = null;
        Record(job, message);
        await db.SaveChangesAsync(ct);
    }

    private void Record(ImportJob job, string message)
    {
        job.UpdatedAt = Now;
        job.Events.Add(new ImportEvent { Message = message, CreatedAt = Now });
    }

    private static (string Title, string Author)? ToTitleAuthor(EmbeddedEpubMetadata? metadata) =>
        metadata is null ? null : (metadata.Title, metadata.Author);
}
