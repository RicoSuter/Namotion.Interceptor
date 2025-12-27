using HomeBlaze.Authorization.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace HomeBlaze.Authorization.Blazor;

/// <summary>
/// A server-side AuthenticationStateProvider that revalidates the authentication state
/// at a regular interval to ensure user changes (roles, deletion) are reflected.
/// </summary>
internal class HomeBlazeAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HomeBlazeAuthenticationStateProvider> _logger;

    /// <summary>
    /// Revalidation interval - how often to check if the user is still valid.
    /// </summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    public HomeBlazeAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger<HomeBlazeAuthenticationStateProvider>();
    }

    /// <summary>
    /// Validates if the user is still valid and their roles haven't changed.
    /// </summary>
    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        // Get the user principal
        var user = authenticationState.User;

        // If not authenticated, nothing to validate
        if (user.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        try
        {
            return await ValidateSecurityStampAsync(userManager, user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating authentication state");
            return false;
        }
    }

    private async Task<bool> ValidateSecurityStampAsync(
        UserManager<ApplicationUser> userManager,
        ClaimsPrincipal principal)
    {
        var userId = userManager.GetUserId(principal);
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user == null)
        {
            // User was deleted
            _logger.LogInformation("User {UserId} no longer exists, invalidating session", userId);
            return false;
        }

        // Validate security stamp if available
        if (userManager.SupportsUserSecurityStamp)
        {
            var principalStamp = principal.FindFirstValue(ClaimTypes.Sid);
            var userStamp = await userManager.GetSecurityStampAsync(user);

            if (!string.Equals(principalStamp, userStamp, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Security stamp changed for user {UserId}, invalidating session",
                    userId);
                return false;
            }
        }

        // Check if user is locked out
        if (await userManager.IsLockedOutAsync(user))
        {
            _logger.LogInformation("User {UserId} is locked out, invalidating session", userId);
            return false;
        }

        return true;
    }
}
