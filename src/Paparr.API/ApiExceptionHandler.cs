using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Paparr.API.Services;

namespace Paparr.API;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            ImportConflictException conflict => (409, "Import conflict", conflict.Message),
            DbUpdateConcurrencyException => (409, "Import changed", "Another request updated this import. Refresh and try again."),
            _ => (500, "Request failed", "The request could not be completed. Check the server logs using the trace identifier.")
        };
        if (status == 500) logger.LogError(exception, "Request {TraceId} failed", context.TraceIdentifier);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Extensions = { ["traceId"] = context.TraceIdentifier }
        }, cancellationToken);
        return true;
    }
}
