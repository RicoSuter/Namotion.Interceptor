using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Checks the session cookie on every request: against the stored security stamp every 5 minutes, and at once against
/// what the <see cref="UserStore"/> holds in memory, see <see cref="UserStore.IsSessionAllowed"/>, so that an endpoint
/// outside the app does not serve the session of a person who was removed, locked or reset since until the next stamp
/// check.
/// </summary>
internal static class SessionValidation
{
    /// <summary>Adds the checks to the Identity application cookie; call it after <c>AddIdentityCookies</c>.</summary>
    public static void AddSessionValidation(this IServiceCollection services)
    {
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(5));
        services.ConfigureApplicationCookie(options =>
        {
            var validateSecurityStamp = options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal = async context =>
            {
                await validateSecurityStamp(context);
                await RejectAsync(context);
            };
        });
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is not { } principal)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var identity = UserIdentity.From(principal, services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.UserIdClaimType);
        if (identity.UserId is { } userId && services.GetRequiredService<UserStore>().IsSessionAllowed(userId, identity.MustChangePassword))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
