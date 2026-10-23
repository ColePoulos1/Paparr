namespace Paparr.API;

public sealed class BrowserRequestGuard(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
        {
            var crossSite = request.Headers["Sec-Fetch-Site"] == "cross-site";
            if (crossSite || (request.Headers.ContainsKey("Origin") && !HasMatchingOrigin(request)))
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Requests from another origin are not allowed. Open Paparr directly to continue.").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }

    private static bool HasMatchingOrigin(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0) return false;
        // The proxy preserves the public Host, including its port. TLS may terminate at the proxy.
        return Uri.TryCreate($"{origin.Scheme}://{request.Host}", UriKind.Absolute, out var target) &&
            origin.Authority.Equals(target.Authority, StringComparison.OrdinalIgnoreCase);
    }
}
