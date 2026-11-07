using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authentication.Blazor.Components;

/// <summary>What the password dialogs read about the password of the signed-in person. Thread-safe.</summary>
internal sealed class AccountPasswords(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Whether the user with <paramref name="userId"/> has a password, read in a scope of its own, as a circuit outlives
    /// the user manager of a request; true when the user is not known, so that the current password is asked for.
    /// </summary>
    public async Task<bool> HasPasswordAsync(string? userId)
    {
        if (userId is null)
        {
            return true;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.FindByIdAsync(userId) is not { } user || await userManager.HasPasswordAsync(user);
    }
}
