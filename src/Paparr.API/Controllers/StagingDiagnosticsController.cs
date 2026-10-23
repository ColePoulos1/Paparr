using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Paparr.API.Data;
using Paparr.API.Domain;
using Paparr.API.Models;

namespace Paparr.API.Controllers;

[ApiController]
[Route("api/diagnostics/staging")]
public sealed class StagingDiagnosticsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<StagingStatusDto>> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var counts = await (from stage in db.Set<PublicationStage>().AsNoTracking()
                            join job in db.ImportJobs on stage.ImportJobId equals job.Id
                            group stage by new
                            {
                                stage.Status,
                                Current = job.Status == ImportStatus.Publishing && job.PublicationFence == stage.PublicationFence
                            } into groupings
                            select new { groupings.Key.Status, groupings.Key.Current, Count = groupings.Count() })
            .ToListAsync(ct);
        return Ok(new StagingStatusDto(
            counts.Where(item => item.Current && item.Status is PublicationStageStatus.Planned or PublicationStageStatus.Owned).Sum(item => item.Count),
            counts.Where(item => !item.Current && item.Status is PublicationStageStatus.Planned or PublicationStageStatus.Owned or PublicationStageStatus.Published).Sum(item => item.Count),
            counts.Where(item => item.Status == PublicationStageStatus.Retained).Sum(item => item.Count),
            counts.Where(item => item.Status == PublicationStageStatus.Cleaned).Sum(item => item.Count)));
    }
}
