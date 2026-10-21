using Microsoft.AspNetCore.Mvc;
using Paparr.API.Services;

namespace Paparr.API.Controllers;

[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController(WorkerDiagnostics diagnostics) : ControllerBase
{
    [HttpGet("worker")]
    public ActionResult<WorkerDiagnosticsSnapshot> GetWorker()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(diagnostics.Snapshot());
    }
}
