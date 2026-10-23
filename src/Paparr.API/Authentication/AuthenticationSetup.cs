using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Paparr.API.Authentication;

public static class AuthenticationSetup
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string StampClaim = "paparr:password-stamp";

    public static IServiceCollection AddOwnerAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ProxySettings.Configure(services, configuration);
        services.AddOptions<AuthenticationSettings>().Bind(configuration.GetSection("Authentication"))
            .Validate(o => !o.Enabled || OwnerPassword.IsValidHash(o.PasswordHash),
                "Set Authentication__PasswordHash using the --hash-password command, or explicitly disable authentication for local development.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeysPath), "Authentication:KeysPath is required.")
            .ValidateOnStart();
        services.AddDataProtection().SetApplicationName("Paparr");
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<AuthenticationSettings>, ILoggerFactory>(
            (options, settings, loggerFactory) =>
                options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(settings.Value.KeysPath), loggerFactory));
        services.AddSingleton<ITicketStore, DatabaseTicketStore>();
        services.AddAuthentication(Scheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(Scheme)
            .Configure<IOptions<AuthenticationSettings>, ITicketStore>((options, settings, store) =>
            {
                options.SessionStore = store;
                options.Cookie.Name = "Paparr.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.IsEssential = true;
                options.Cookie.Path = "/";
                options.Cookie.SecurePolicy = settings.Value.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => Results.Problem(statusCode: 401,
                    detail: "Sign in to continue.").ExecuteAsync(context.HttpContext);
                options.Events.OnRedirectToAccessDenied = context => Results.Problem(statusCode: 403,
                    detail: "This session cannot access Paparr.").ExecuteAsync(context.HttpContext);
                options.Events.OnValidatePrincipal = async context =>
                {
                    if (context.Principal?.FindFirst(StampClaim)?.Value != OwnerPassword.Stamp(settings.Value.PasswordHash))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(Scheme);
                    }
                };
            });
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder(Scheme)
            .AddRequirements(new OwnerAccessRequirement()).Build());
        services.AddSingleton<IAuthorizationHandler, OwnerAccessHandler>();
        services.AddAntiforgery();
        services.AddOptions<AntiforgeryOptions>().Configure<IOptions<AuthenticationSettings>>((options, settings) =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "Paparr.Csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.IsEssential = true;
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = settings.Value.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("login", policy =>
            {
                policy.PermitLimit = 5;
                policy.Window = TimeSpan.FromMinutes(1);
                policy.QueueLimit = 0;
                policy.AutoReplenishment = true;
            });
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: 429, detail: "Too many sign-in attempts. Wait a minute and try again.")
                    .ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }
}
