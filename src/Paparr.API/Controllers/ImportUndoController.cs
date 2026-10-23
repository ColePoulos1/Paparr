using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paparr.API.Data;
using Paparr.API.Models;
using Paparr.API.Services;

namespace Paparr.API.Controllers;

[ApiController]
[Route("api/imports")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ImportUndoController(AppDbContext db, ImportUndoService undo, IOptions<IngestionOptions> options) : ControllerBase
{
    [HttpGet("{id:long}/undo-preview")]
    public async Task<ActionResult<UndoPreviewDto>> Preview(long id, CancellationToken ct)
    {
        var job = await db.ImportJobs.AsNoTracking().Include(j => j.AcceptedBook).SingleOrDefaultAsync(j => j.Id == id, ct);
        return job is null ? NotFound() : Ok(await undo.PreviewAsync(job, ct));
    }

    [HttpPost("{id:long}/undo")]
    public async Task<ActionResult<ImportJobDto>> Start(long id, StartUndoRequest request, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || request.Revision == Guid.Empty)
            return ValidationProblem("Provide an undo operation ID and the revision from the current preview.");
        if (!await db.ImportJobs.AnyAsync(j => j.Id == id, ct)) return NotFound();
        await undo.StartAsync(id, request.Revision, request.OperationId, ct);
        return Ok(await ReadAsync(id, ct));
    }

    [HttpPost("{id:long}/undo/retry")]
    public async Task<ActionResult<ImportJobDto>> Retry(long id, RetryUndoRequest request, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty) return ValidationProblem("Provide the undo operation ID to retry.");
        if (!await db.ImportJobs.AnyAsync(j => j.Id == id, ct)) return NotFound();
        await undo.RetryAsync(id, request.OperationId, ct);
        return Ok(await ReadAsync(id, ct));
    }

    private async Task<ImportJobDto> ReadAsync(long id, CancellationToken ct) => ImportJobDto.From(
        await db.ImportJobs.AsNoTracking().Include(j => j.Candidates).Include(j => j.AcceptedBook).Include(j => j.Events)
            .AsSplitQuery().SingleAsync(j => j.Id == id, ct), options.Value.DeleteSourceAfterImport);
}
