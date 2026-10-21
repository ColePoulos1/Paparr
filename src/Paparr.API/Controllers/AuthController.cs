using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Paparr.API.Authentication;

namespace Paparr.API.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(IOptions<AuthenticationSettings> settings, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("session")]
    public ActionResult<SessionDto> Session() => SessionState();

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<SessionDto>> Login(LoginRequest request)
    {
        if (!settings.Value.Enabled) return SessionState();
        if (!OwnerPassword.Verify(settings.Value.PasswordHash, request.Password))
            return Problem(statusCode: 401, detail: "The password is incorrect.");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "owner"),
            new Claim(ClaimTypes.Name, "Owner"),
            new Claim(AuthenticationSetup.StampClaim, OwnerPassword.Stamp(settings.Value.PasswordHash))
        ], AuthenticationSetup.Scheme));
        await HttpContext.SignInAsync(AuthenticationSetup.Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false
        });
        HttpContext.User = principal;
        return SessionState();
    }

    [HttpPost("logout")]
    public async Task<ActionResult<SessionDto>> Logout()
    {
        await HttpContext.SignOutAsync(AuthenticationSetup.Scheme);
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        return SessionState();
    }

    private SessionDto SessionState() => new(settings.Value.Enabled,
        !settings.Value.Enabled || User.Identity?.IsAuthenticated == true,
        settings.Value.Enabled ? antiforgery.GetAndStoreTokens(HttpContext).RequestToken : null);
}

public sealed record LoginRequest([Required, StringLength(512)] string Password);
public sealed record SessionDto(bool Enabled, bool Authenticated, string? CsrfToken);
