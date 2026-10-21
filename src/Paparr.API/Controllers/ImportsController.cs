using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;
using Paparr.API.Services;

namespace Paparr.API.Controllers;

[ApiController]
[Route("api/imports")]
public sealed class ImportsController(AppDbContext db, IEbookIngestionService ingestion,
    IOptions<IngestionOptions> options, TimeProvider clock, SourceCleanupService cleanup) : ControllerBase
{
    private ImportJobDto ToDto(ImportJob job) => ImportJobDto.From(job, options.Value.DeleteSourceAfterImport);
    private IQueryable<ImportJob> Details => db.ImportJobs.Include(j => j.Candidates)
        .Include(j => j.AcceptedBook).Include(j => j.Events).AsSplitQuery();

    [HttpGet]
    public async Task<ActionResult<PagedResult<ImportJobDto>>> GetImports(CancellationToken ct,
        ImportStatus? status = null, string? search = null, int page = 1, int pageSize = 25)
    {
        if (page < 1 || page > 100000 || pageSize is < 1 or > 100 || search?.Length > 200 ||
            (status.HasValue && !Enum.IsDefined(status.Value)))
            return ValidationProblem("Use a valid page, page size from 1 to 100, and search of at most 200 characters.");
        var query = Details.AsNoTracking();
        if (status.HasValue) query = query.Where(j => j.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(j => j.FilePath.ToLower().Contains(term) ||
                j.Candidates.Any(c => c.Title.ToLower().Contains(term) || c.Author.ToLower().Contains(term)));
        }
        var total = await query.CountAsync(ct);
        var jobs = await query.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new PagedResult<ImportJobDto>(jobs.Select(ToDto).ToList(), total, page, pageSize));
    }

    [HttpGet("summary")]
    public async Task<ActionResult<Dictionary<string, int>>> GetSummary(CancellationToken ct)
    {
        var counts = await db.ImportJobs.GroupBy(j => j.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        return Ok(Enum.GetValues<ImportStatus>().ToDictionary(s => s.ToString(), s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ImportJobDto>> GetImport(long id, CancellationToken ct)
    {
        var job = await Details.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return job == null ? NotFound() : Ok(ToDto(job));
    }

    [HttpGet("{id:long}/duplicates")]
    public async Task<ActionResult<PagedResult<DuplicateSourceDto>>> GetDuplicateSources(long id,
        CancellationToken ct, int page = 1, int pageSize = 25)
    {
        if (page < 1 || page > 100000 || pageSize is < 1 or > 100)
            return ValidationProblem("Use a valid page and page size from 1 to 100.");
        if (!await db.ImportJobs.AnyAsync(j => j.Id == id, ct)) return NotFound();

        var query = db.DuplicateSources.AsNoTracking().Where(d => d.ImportJobId == id);
        var total = await query.CountAsync(ct);
        var sources = await query.OrderBy(d => d.DiscoveredAt).ThenBy(d => d.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = sources.Select(source => new DuplicateSourceDto(source.Id, SafeFileName(source.FilePath),
            GetRelativeSourcePath(source.FilePath), source.DiscoveredAt)).ToList();
        return Ok(new PagedResult<DuplicateSourceDto>(items, total, page, pageSize));
    }

    private string? GetRelativeSourcePath(string path)
    {
        try
        {
            var root = Path.GetFullPath(options.Value.IngestPath);
            if (!LibraryPaths.IsWithin(root, path)) return null;
            return Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string SafeFileName(string path) => path
        .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
        .LastOrDefault() ?? "";

    [HttpGet("{id:long}/preview/{candidateId:long}")]
    public async Task<ActionResult<ImportPreviewDto>> Preview(long id, long candidateId, CancellationToken ct)
    {
        var job = await Details.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job == null) return NotFound();
        var candidate = job.Candidates.FirstOrDefault(c => c.Id == candidateId);
        if (candidate == null) return NotFound();
        var destination = LibraryPaths.Destination(options.Value.LibraryPath, candidate.Title, candidate.Author,
            Path.GetExtension(job.FilePath), job.FileHash);
        return Ok(new ImportPreviewDto(Path.GetRelativePath(options.Value.LibraryPath, destination).Replace('\\', '/'),
            System.IO.File.Exists(destination), !options.Value.DeleteSourceAfterImport));
    }

    [HttpPost("{id:long}/accept/{candidateId:long}")]
    public async Task<ActionResult<ImportJobDto>> AcceptCandidate(long id, long candidateId, CancellationToken ct)
    {
        var job = await Details.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job == null) return NotFound();
        var candidate = job.Candidates.FirstOrDefault(c => c.Id == candidateId);
        if (candidate == null) return NotFound();
        await ingestion.AcceptCandidateAsync(job, candidate, ct);
        return Ok(ToDto(await Details.AsNoTracking().SingleAsync(j => j.Id == id, ct)));
    }

    [HttpPost("{id:long}/metadata")]
    public async Task<ActionResult<ImportJobDto>> EditMetadata(long id, EditMetadataRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Author))
            return ValidationProblem("Title and author cannot be blank.");
        var job = await Details.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job == null) return NotFound();
        if (job.Status != ImportStatus.AwaitingApproval) throw new ImportConflictException("Metadata can only be edited while awaiting review.");
        var now = clock.GetUtcNow().UtcDateTime;
        job.Candidates.Add(new MetadataCandidate
        {
            Title = request.Title.Trim(),
            Author = request.Author.Trim(),
            Source = "manual",
            ExternalId = "",
            ConfidenceScore = 100,
            Evidence = "Entered during manual review.",
            CreatedAt = now
        });
        job.UpdatedAt = now;
        job.Events.Add(new ImportEvent { Message = "Added a manual metadata choice.", CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(job));
    }

    [HttpPost("{id:long}/cleanup/retry")]
    public async Task<ActionResult<ImportJobDto>> RetryCleanup(long id, CancellationToken ct)
    {
        var job = await Details.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job == null) return NotFound();
        await cleanup.QueueRetryAsync(job, ct);
        return Ok(ToDto(job));
    }

    [HttpPost("{id:long}/retry")]
    public async Task<ActionResult<ImportJobDto>> RetryImport(long id, CancellationToken ct)
    {
        var job = await Details.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job == null) return NotFound();
        if (job.Status == ImportStatus.Pending) return Ok(ToDto(job));
        if (job.Status != ImportStatus.Failed) throw new ImportConflictException("Only failed imports can be retried.");
        if (job.AttemptCount >= options.Value.MaxAttempts)
            throw new ImportConflictException("The attempt limit was reached. Check the file and increase MaxAttempts to retry.");
        job.Status = ImportStatus.Pending;
        job.LastError = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        job.Events.Add(new ImportEvent { Message = "Retry queued.", CreatedAt = job.UpdatedAt });
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(job));
    }
}
