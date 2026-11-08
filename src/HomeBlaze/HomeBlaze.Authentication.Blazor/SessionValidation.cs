using System.Security.Claims;
using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
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
/// signed in for that request only, and its cookie is kept; when it carries another stamp than the store holds for a person,
/// the store reloads that person, see <see cref="UserStore.RequestStampReload"/>. A renewal of the cookie, by the stamp check or the sliding
/// expiration, is not written when the store no longer allows its session as the response starts.
/// </summary>
internal static class SessionValidation
{
    private static readonly object RenewedPrincipalKey = new();

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
                // Rejecting keeps the cookie, which an ended session presents in vain on every request: a parallel request
                // right after a password change carries the old stamp until the browser stored the new cookie. Until the
                // database is ready, only the requests that AuthenticationUnavailableMiddleware lets through, such as
                // static assets and the health endpoints, get here, and the users may not be loaded yet.
                if (!context.HttpContext.RequestServices.GetRequiredService<AuthenticationDatabase>().IsReady || !IsAllowedByStore(context))
                {
                    context.RejectPrincipal();
                    return Task.CompletedTask;
                }

                return ValidateAsync(context, validateSecurityStamp);
            };

            var checkSlidingExpiration = options.Events.OnCheckSlidingExpiration;
            options.Events.OnCheckSlidingExpiration = async context =>
            {
                await checkSlidingExpiration(context);
                if (context.ShouldRenew)
                {
                    context.HttpContext.Items[RenewedPrincipalKey] = context.Principal;
                }
            };

            // A sign-in writes a cookie of its own, which carries the stamp it was built with, in place of the renewal.
            var signingIn = options.Events.OnSigningIn;
            options.Events.OnSigningIn = context =>
            {
                context.HttpContext.Items.Remove(RenewedPrincipalKey);
                return signingIn(context);
            };

            options.CookieManager = new RenewalCheckingCookieManager(options.CookieManager ?? new ChunkingCookieManager());
        });
    }

    private static async Task ValidateAsync(CookieValidatePrincipalContext context, Func<CookieValidatePrincipalContext, Task> validateSecurityStamp)
    {
        await validateSecurityStamp(context);
        if (context.ShouldRenew && context.Principal is not null)
        {
            context.HttpContext.Items[RenewedPrincipalKey] = context.Principal;
        }
    }

    // A refused stamp of a person the store knows may be one the store missed, such as after a reload that failed once a
    // password change was committed, so the store reloads the user for the requests to come.
    private static bool IsAllowedByStore(CookieValidatePrincipalContext context)
    {
        if (context.Principal is not { } principal)
        {
            return true;
        }

        var (userStore, userId, securityStamp) = GetSession(context.HttpContext, principal);
        if (userId is null)
        {
            return false;
        }

        if (userStore.IsSessionAllowed(userId, securityStamp))
        {
            return true;
        }

        _ = userStore.RequestStampReload(userId, securityStamp);
        return false;
    }

    private static bool IsAllowedByStore(HttpContext context, ClaimsPrincipal principal)
    {
        var (userStore, userId, securityStamp) = GetSession(context, principal);
        return userId is not null && userStore.IsSessionAllowed(userId, securityStamp);
    }

    private static (UserStore UserStore, string? UserId, string? SecurityStamp) GetSession(HttpContext context, ClaimsPrincipal principal)
    {
        var services = context.RequestServices;
        var claimTypes = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        return (services.GetRequiredService<UserStore>(),
            principal.FindFirstValue(claimTypes.UserIdClaimType),
            principal.FindFirstValue(claimTypes.SecurityStampClaimType));
    }

    // The cookie handler writes a renewed cookie only as the response starts, which can be after a password change that a
    // request accepted before it did not see; its cookie with the old stamp would then replace the new cookie in the browser.
    private sealed class RenewalCheckingCookieManager(ICookieManager inner) : ICookieManager
    {
        public string? GetRequestCookie(HttpContext context, string key) => inner.GetRequestCookie(context, key);

        public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
        {
            if (context.Items.TryGetValue(RenewedPrincipalKey, out var renewed) && renewed is ClaimsPrincipal principal && !IsAllowedByStore(context, principal))
            {
                return;
            }

            inner.AppendResponseCookie(context, key, value, options);
        }

        public void DeleteCookie(HttpContext context, string key, CookieOptions options) => inner.DeleteCookie(context, key, options);
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
