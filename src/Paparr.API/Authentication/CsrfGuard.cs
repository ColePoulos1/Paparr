using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace Paparr.API.Authentication;

public sealed class CsrfGuard(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery, IOptions<AuthenticationSettings> settings)
    {
        var request = context.Request;
        if (settings.Value.Enabled && request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    detail: "Your request could not be verified. Refresh Paparr and try again.").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
}
