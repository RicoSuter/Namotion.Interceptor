using System.Security.Claims;
using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Checks the session cookie on every request: at once against what the <see cref="UserStore"/> holds in memory, the
/// security stamp included, see <see cref="UserStore.IsSessionAllowed"/>, so that an endpoint outside the app does not
/// serve a session that a removal, a lock, a reset or a password change made through the store ended, and against the
/// stored security stamp every 5 minutes, see <see cref="SessionStampValidator"/>. A refused session is treated as not
/// signed in for that request only, and its cookie is kept.
/// </summary>
internal static class SessionValidation
{
    /// <summary>Adds the checks to the Identity application cookie; call it after <c>AddIdentityCookies</c>.</summary>
    public static void AddSessionValidation(this IServiceCollection services)
    {
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(5));
        services.Replace(ServiceDescriptor.Scoped<ISecurityStampValidator, SessionStampValidator>());
        services.ConfigureApplicationCookie(options =>
        {
            var validateSecurityStamp = options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal = context =>
            {
                // Rejecting keeps the cookie, which the store refuses on every request anyway: the store is empty while the
                // users load at startup, and holds a new stamp before the browser stored the cookie that carries it. The
                // database is not touched before it is ready.
                if (!context.HttpContext.RequestServices.GetRequiredService<AuthenticationDatabase>().IsReady || !IsAllowedByStore(context))
                {
                    context.RejectPrincipal();
                    return Task.CompletedTask;
                }

                return validateSecurityStamp(context);
            };
        });
    }

    private static bool IsAllowedByStore(CookieValidatePrincipalContext context)
    {
        if (context.Principal is not { } principal)
        {
            return true;
        }

        var services = context.HttpContext.RequestServices;
        var claimTypes = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        var userId = principal.FindFirstValue(claimTypes.UserIdClaimType);
        return userId is not null &&
            services.GetRequiredService<UserStore>().IsSessionAllowed(userId, principal.FindFirstValue(claimTypes.SecurityStampClaimType));
    }
}

/// <summary>
/// Identity's check of the session cookie against the stored security stamp once the validation interval passed, which
/// renews the cookie with a principal built anew when the stamp matches, but only rejects the principal of the request
/// when it does not, and keeps the cookie.
/// </summary>
/// <remarks>
/// A parallel request right after a password change still carries the cookie with the old stamp before the browser stored
/// the new one; Identity's own validator would sign out and delete the cookie, ending the session that is valid.
/// </remarks>
internal sealed class SessionStampValidator(
    IOptions<SecurityStampValidatorOptions> options,
    SignInManager<ApplicationUser> signInManager,
    ILoggerFactory loggerFactory)
    : SecurityStampValidator<ApplicationUser>(options, signInManager, loggerFactory)
{
    /// <inheritdoc />
    public override async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Properties.IssuedUtc is { } issuedUtc && TimeProvider.GetUtcNow() - issuedUtc <= Options.ValidationInterval)
        {
            return;
        }

        if (await VerifySecurityStamp(context.Principal) is { } user)
        {
            await SecurityStampVerified(user, context);
        }
        else
        {
            context.RejectPrincipal();
        }
    }
}
