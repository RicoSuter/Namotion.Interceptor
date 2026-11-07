using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Registers what the person of a circuit may see and do, and its policies for ASP.NET Core authorization.</summary>
internal static class PermissionServiceCollectionExtensions
{
    public static void AddPermissionServices(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
            options.AddPolicy(HomeBlazePolicies.FullAdministrator, builder => builder.AddRequirements(new FullAdministratorRequirement())));
        services.AddSingleton<IAuthorizationHandler, FullAdministratorHandler>();
        services.AddSingleton<IAuthorizationHandler, MemberActionHandler>();

        // Added, not tried, so that they replace the defaults of the HomeBlaze services, which deny everything.
        services.AddScoped<AuthenticationStateProvider, HomeBlazeRevalidatingAuthenticationStateProvider>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<IUserPermissions, PolicyUserPermissions>();
        services.AddScoped<IUserAdministration, CurrentUserAdministration>();
    }
}
