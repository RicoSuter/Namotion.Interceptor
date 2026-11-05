using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using AuthorizationService = HomeBlaze.Authorization.AuthorizationService;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Decides the <see cref="FullAdministratorRequirement"/>.</summary>
internal sealed class FullAdministratorHandler(UserStore userStore, AuthorizationService authorizationService, IOptions<IdentityOptions> identityOptions)
    : AuthorizationHandler<FullAdministratorRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FullAdministratorRequirement requirement)
    {
        var identity = UserIdentity.From(context.User, identityOptions.Value.ClaimsIdentity.UserIdClaimType);
        if (identity.IsSignedIn && PermissionDecisions.Create(authorizationService, identity.GetRoles(userStore), identity.Kind).IsFullAdministrator)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
