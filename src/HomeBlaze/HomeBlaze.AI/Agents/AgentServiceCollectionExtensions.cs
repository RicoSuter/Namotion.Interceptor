using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Registers the services of the built-in agents.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Adds the agent services, including a hosted service that serves the agent MCP route of CLI agents on a
    /// loopback listener of its own.
    /// </summary>
    public static IServiceCollection AddHomeBlazeAgents(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AgentMcpListener)))
        {
            return services;
        }

        services.AddSingleton<AgentMcpListener>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<AgentMcpListener>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AgentTurnTokenRegistry>();
        services.TryAddSingleton<IAgentToolSource, HomeBlazeAgentToolSource>();
        services.TryAddSingleton<IAgentEndpointAddress>(serviceProvider => serviceProvider.GetRequiredService<AgentMcpListener>());
        services.TryAddSingleton<AgentServices>();
        return services;
    }
}
