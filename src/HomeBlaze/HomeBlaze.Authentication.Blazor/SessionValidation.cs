using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Checks the session cookie on every request: against the stored security stamp every 5 minutes, and at once against
/// what the <see cref="UserStore"/> holds in memory, the security stamp included, see <see cref="UserStore.IsSessionAllowed"/>,
/// so that an endpoint outside the app does not serve a session that a removal, a lock, a reset or a password change
/// made through the store ended, until the next stamp check or ever after, once the person is active again.
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
                Reject(context);
            };
        });
    }

    // Rejects the principal of this request only and keeps the cookie, which the store refuses on every request anyway: the
    // store is empty while the users load at startup, and holds a new stamp before the browser stored the cookie that
    // carries it, so deleting the cookie would end sessions that are valid.
    private static void Reject(CookieValidatePrincipalContext context)
    {
        if (context.Principal is not { } principal)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var claimTypes = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        var userId = principal.FindFirstValue(claimTypes.UserIdClaimType);
        var securityStamp = principal.FindFirstValue(claimTypes.SecurityStampClaimType);
        if (userId is not null && services.GetRequiredService<UserStore>().IsSessionAllowed(userId, securityStamp))
        {
            return;
        }

        context.RejectPrincipal();
    }
}
