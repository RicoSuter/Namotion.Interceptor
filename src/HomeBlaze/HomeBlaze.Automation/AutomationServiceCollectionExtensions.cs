using HomeBlaze.Automation.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeBlaze.Automation;

/// <summary>Registers the services of automations.</summary>
public static class AutomationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services that automations take. An <see cref="IAutomationAuthorizer"/> registered before is kept; without
    /// one, every operation call and property write is allowed, except operations that require confirmation.
    /// </summary>
    public static IServiceCollection AddHomeBlazeAutomation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAutomationAuthorizer, AllowAllAutomationAuthorizer>();
        services.TryAddSingleton<AutomationChangeDispatcher>();
        services.TryAddSingleton<AutomationServices>();
        return services;
    }
}
