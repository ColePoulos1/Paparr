using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Paparr.API.Authentication;

public sealed class OwnerAccessRequirement : IAuthorizationRequirement;

public sealed class OwnerAccessHandler(IOptions<AuthenticationSettings> settings) : AuthorizationHandler<OwnerAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnerAccessRequirement requirement)
    {
        if (!settings.Value.Enabled || context.User.Identity?.IsAuthenticated == true) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
