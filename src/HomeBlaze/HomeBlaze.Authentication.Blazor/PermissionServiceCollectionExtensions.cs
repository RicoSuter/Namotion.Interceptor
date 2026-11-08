using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Registers what the person of a circuit, or of a request outside a circuit, may see and do.</summary>
internal static class PermissionServiceCollectionExtensions
{
    public static void AddPermissionServices(this IServiceCollection services)
    {
        // Added, not tried, so that they replace the defaults of the HomeBlaze services, which deny everything.
        services.AddScoped<AuthenticationStateProvider, HomeBlazeRevalidatingAuthenticationStateProvider>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<IUserPermissions, PolicyUserPermissions>();
        services.AddScoped<IUserAdministration>(serviceProvider => CreateUserAdministration(
            serviceProvider.GetRequiredService<UserStore>(), serviceProvider.GetRequiredService<CurrentUser>()));
        services.AddSingleton<IRequestUserPermissionsFactory, RequestUserPermissionsFactory>();
    }

    /// <summary>
    /// Creates the user administration of a circuit: every call acts as the person signed in at that moment, so that a
    /// circuit signed out meanwhile, by the person or by revalidation, refuses every change, as it does while the person
    /// must change their password.
    /// </summary>
    internal static UserAdministration CreateUserAdministration(UserStore userStore, CurrentUser currentUser) =>
        new(userStore, () => currentUser.UserId is { } userId && !userStore.RequiresPasswordChange(userId) ? userId : null);
}
