using HomeBlaze.Authorization;
using HomeBlaze.Automation.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeBlaze.Automation;

/// <summary>Registers the services of automations.</summary>
public static class AutomationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services that automations take. An <see cref="IAutomationAuthorizer"/> registered before is kept; without
    /// one, <see cref="RoleAutomationAuthorizer"/> decides with the roles of each automation and needs the
    /// <see cref="AuthorizationService"/> that
    /// <see cref="HomeBlaze.Authorization.AuthorizationServiceCollectionExtensions.AddHomeBlazeAuthorization"/> registers.
    /// </summary>
    public static IServiceCollection AddHomeBlazeAutomation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAutomationAuthorizer, RoleAutomationAuthorizer>();
        services.TryAddSingleton<AutomationChangeDispatcher>();
        return services;
    }
}
