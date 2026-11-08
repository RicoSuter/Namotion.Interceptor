using System.Security.Claims;
using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication;

/// <summary>The claim types HomeBlaze adds to the principal of a signed-in person.</summary>
public static class HomeBlazeClaimTypes
{
    /// <summary>Present, with the value <c>true</c>, while the person must set a password before using anything else.</summary>
    public const string MustChangePassword = "homeblaze:must_change_password";
}

/// <summary>
/// Creates the principal of a signed-in person with their id, name, security stamp and, as
/// <see cref="ApplicationUser.RequiresPasswordChange"/> decides, the <see cref="HomeBlazeClaimTypes.MustChangePassword"/> claim, and no role claims: the roles are read from the user store
/// on every decision, and a change of roles keeps the security stamp, so role claims would go stale.
/// </summary>
public sealed class HomeBlazeClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser>
{
    public HomeBlazeClaimsPrincipalFactory(UserManager<ApplicationUser> userManager, IOptions<IdentityOptions> options)
        : base(userManager, options)
    {
    }

    /// <inheritdoc />
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.RequiresPasswordChange())
        {
            identity.AddClaim(new Claim(HomeBlazeClaimTypes.MustChangePassword, "true"));
        }

        return identity;
    }
}
