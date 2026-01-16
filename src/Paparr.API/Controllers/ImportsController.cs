using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;
using Paparr.API.Services;

namespace Paparr.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEbookIngestionService _ingestionService;
    private readonly ILogger<ImportsController> _logger;

    public ImportsController(
        AppDbContext db,
        IEbookIngestionService ingestionService,
        ILogger<ImportsController> logger)
    {
        _db = db;
        _ingestionService = ingestionService;
        _logger = logger;
    }

    /// <summary>
    /// Get all import jobs with their candidates.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ImportJobDto>>> GetImports()
    {
        var jobs = await _db.ImportJobs
            .AsNoTracking()
            .Include(j => j.Candidates)
            .Include(j => j.AcceptedBook)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        return Ok(jobs.Select(MapToDto).ToList());
    }

    /// <summary>
    /// Get a specific import job with its candidates.
    /// </summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportJobDto>> GetImport(long id)
    {
        var job = await _db.ImportJobs
            .AsNoTracking()
            .Include(j => j.Candidates)
            .Include(j => j.AcceptedBook)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null)
            return NotFound();

        return Ok(MapToDto(job));
    }

    /// <summary>
    /// Accept a metadata candidate for an import job.
    /// Creates the Book record and moves file to library.
    /// </summary>
    [HttpPost("{id:long}/accept/{candidateId:long}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportJobDto>> AcceptCandidate(long id, long candidateId)
    {
        var job = await _db.ImportJobs
            .Include(j => j.Candidates)
            .Include(j => j.AcceptedBook)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null)
            return NotFound("Import job not found");

        var candidate = job.Candidates.FirstOrDefault(c => c.Id == candidateId);
        if (candidate == null)
            return BadRequest("Candidate not found for this job");

        try
        {
            await _ingestionService.AcceptCandidateAsync(job, candidate);
            
            // Reload to get updated state
            await _db.Entry(job).ReloadAsync();
            await _db.Entry(job).Collection(j => j.Candidates).LoadAsync();
            await _db.Entry(job).Reference(j => j.AcceptedBook).LoadAsync();

            return Ok(MapToDto(job));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting candidate {CandidateId} for job {JobId}", candidateId, id);
            return BadRequest("Error accepting candidate");
        }
    }

    /// <summary>
    /// Retry a failed import job.
    /// Resets job to Pending status for reprocessing.
    /// </summary>
    [HttpPost("{id:long}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportJobDto>> RetryImport(long id)
    {
        var job = await _db.ImportJobs
            .Include(j => j.Candidates)
            .Include(j => j.AcceptedBook)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null)
            return NotFound();

        if (job.Status != ImportStatus.Failed)
            return BadRequest("Only failed jobs can be retried");

        // Clear previous candidates
        _db.MetadataCandidates.RemoveRange(job.Candidates);

        job.Status = ImportStatus.Pending;
        job.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(MapToDto(job));
    }

    private ImportJobDto MapToDto(ImportJob job)
    {
        return new ImportJobDto
        {
            Id = job.Id,
            FilePath = job.FilePath,
            Status = job.Status.ToString(),
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            Candidates = job.Candidates
                .OrderByDescending(c => c.ConfidenceScore)
                .Select(c => new MetadataCandidateDto
                {
                    Id = c.Id,
                    Title = c.Title,
                    Author = c.Author,
                    Source = c.Source,
                    ExternalId = c.ExternalId,
                    ConfidenceScore = c.ConfidenceScore
                })
                .ToList(),
            AcceptedBook = job.AcceptedBook != null ? new BookDto
            {
                Id = job.AcceptedBook.Id,
                Title = job.AcceptedBook.Title,
                Author = job.AcceptedBook.Author,
                Source = job.AcceptedBook.Source,
                ExternalId = job.AcceptedBook.ExternalId,
                ImportedAt = job.AcceptedBook.ImportedAt
            } : null
        };
    }
}
