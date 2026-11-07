using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Namotion.Interceptor.Registry.Abstractions;
using AuthorizationService = HomeBlaze.Authorization.AuthorizationService;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Decides the <see cref="MemberActionRequirement"/> on a <see cref="RegisteredSubjectProperty"/>.</summary>
/// <remarks>
/// Compiles the decisions of the principal on every call; code that renders many members, or renders often, uses the
/// scoped <see cref="HomeBlaze.Authorization.Abstractions.IUserPermissions"/>, which keeps them until they change.
/// </remarks>
internal sealed class MemberActionHandler(UserStore userStore, AuthorizationService authorizationService, IOptions<IdentityOptions> identityOptions)
    : AuthorizationHandler<MemberActionRequirement, RegisteredSubjectProperty>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MemberActionRequirement requirement, RegisteredSubjectProperty resource)
    {
        var identity = UserIdentity.From(context.User, identityOptions.Value.ClaimsIdentity.UserIdClaimType);
        var decisions = PermissionDecisions.Create(authorizationService, identity.GetRoles(userStore), identity.Kind);
        if (requirement.IsAllowed(decisions, resource))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
