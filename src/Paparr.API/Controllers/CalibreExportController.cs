using Microsoft.AspNetCore.Mvc;
using Paparr.API.Services;

namespace Paparr.API.Controllers;

[ApiController]
[Route("api/library")]
public sealed class CalibreExportController(CalibreExportService exports) : ControllerBase
{
    [HttpGet("{id:long}/export")]
    public async Task<IActionResult> Export(long id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        try
        {
            var export = await exports.CreateAsync(id, ct);
            return export is null
                ? NotFound()
                : File(export.Stream, export.ContentType, export.FileName);
        }
        catch (LibraryDownloadConflictException)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The stored book file is unavailable or has changed.",
                detail: "Run a library integrity scan and check the configured library permissions.");
        }
        catch (CalibreExportMetadataException)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Approved book metadata is too large to export.",
                detail: "Download the original file and edit its metadata in Calibre.");
        }
    }
}
