using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Authorization;
using AuthorizationService = HomeBlaze.Authorization.AuthorizationService;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Requires a signed-in person, or, for a visitor who is not signed in, that the built-in user
/// <see cref="NameRules.AnonymousUserName"/> holds roles that compile into a policy. The fallback policy of every
/// endpoint that allows no anonymous access.
/// </summary>
public sealed class HomeBlazeAccessRequirement : IAuthorizationRequirement
{
    /// <summary>The name of the policy made of this requirement.</summary>
    public const string PolicyName = "HomeBlazeAccess";
}

/// <summary>Decides the <see cref="HomeBlazeAccessRequirement"/>.</summary>
internal sealed class HomeBlazeAccessHandler(UserStore userStore, AuthorizationService authorizationService)
    : AuthorizationHandler<HomeBlazeAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, HomeBlazeAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true || IsAnonymousAllowed())
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private bool IsAnonymousAllowed()
    {
        var anonymousRoles = userStore.AnonymousRoles;
        return anonymousRoles.Count > 0 && authorizationService.GetPolicy(anonymousRoles, PrincipalKind.Anonymous).Error is null;
    }
}
