using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;
using Paparr.API.Services;

namespace Paparr.API.Controllers;

[ApiController]
[Route("api/library")]
public sealed class LibraryController(AppDbContext db, LibraryAuditService audits, IOptions<IngestionOptions> options,
    LibraryDownloadService downloads) : ControllerBase
{
    [HttpGet("{id:long}/file")]
    public async Task<IActionResult> Download(long id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        LibraryDownload? download;
        try
        {
            download = await downloads.OpenVerifiedAsync(id, ct);
        }
        catch (LibraryDownloadConflictException)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The stored book file is unavailable or has changed.",
                detail: "Run a library integrity scan and check the configured library permissions.");
        }
        if (download is null) return NotFound();

        return File(download.Stream, download.ContentType, download.FileName, enableRangeProcessing: true);
    }

    [HttpGet]
    public async Task<ActionResult<LibraryPageDto>> GetBooks(CancellationToken ct,
        IntegrityStatus? status = null, int page = 1, int pageSize = 25)
    {
        if (page is < 1 or > 100000 || pageSize is < 1 or > 100 ||
            (status.HasValue && !Enum.IsDefined(status.Value)))
            return ValidationProblem("Use a valid page and page size from 1 to 100.");
        var query = db.Books.AsNoTracking();
        if (status.HasValue) query = query.Where(b => b.IntegrityStatus == status);
        var total = await query.CountAsync(ct);
        var books = await query.OrderByDescending(b => b.ImportedAt).ThenByDescending(b => b.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var counts = await db.Books.GroupBy(b => b.IntegrityStatus).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        return Ok(new LibraryPageDto(books.Select(b => new LibraryBookDto(b.Id, b.Title, b.Author,
            b.FilePath is { } path && LibraryPaths.IsWithin(options.Value.LibraryPath, path)
                ? Path.GetRelativePath(options.Value.LibraryPath, path).Replace('\\', '/') : null,
            b.ImportedAt, b.IntegrityStatus.ToString(), b.IntegrityCheckedAt, b.IntegrityMessage,
            EditionDto.From(b.Isbn13, b.IsbnStatus, b.Publisher, b.PublicationYear, b.Language))).ToList(),
            total, page, pageSize, Enum.GetValues<IntegrityStatus>().ToDictionary(s => s.ToString(),
                s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0)));
    }

    [HttpGet("audits/latest")]
    public async Task<ActionResult<LatestLibraryAuditDto>> GetLatest(CancellationToken ct)
    {
        var audit = await db.LibraryAudits.AsNoTracking().OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        return Ok(new LatestLibraryAuditDto(audit is null ? null : LibraryAuditDto.From(audit)));
    }

    [HttpGet("audits/{id:long}")]
    public async Task<ActionResult<LibraryAuditDto>> GetAudit(long id, CancellationToken ct)
    {
        var audit = await db.LibraryAudits.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        return audit is null ? NotFound() : Ok(LibraryAuditDto.From(audit));
    }

    [HttpPost("audits")]
    public async Task<ActionResult<LibraryAuditDto>> StartAudit(CancellationToken ct)
    {
        var audit = await audits.QueueAsync(ct);
        return Accepted($"/api/library/audits/{audit.Id}", LibraryAuditDto.From(audit));
    }
}
