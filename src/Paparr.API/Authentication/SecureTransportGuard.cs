using Microsoft.Extensions.Options;

namespace Paparr.API.Authentication;

public sealed class SecureTransportGuard(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<AuthenticationSettings> settings)
    {
        if (settings.Value.Enabled && settings.Value.SecureCookies && !context.Request.IsHttps &&
            context.Request.Path.StartsWithSegments("/api"))
        {
            await Results.Problem(statusCode: 400,
                detail: "Paparr requires HTTPS. Check the reverse proxy and its trusted network configuration.")
                .ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}
