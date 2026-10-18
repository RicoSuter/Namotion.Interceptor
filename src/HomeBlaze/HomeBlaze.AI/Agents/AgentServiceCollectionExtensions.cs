using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Registers the services of the built-in agents.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Adds the agent services and hooks the agent route into the MCP HTTP transport, after any session options
    /// callback configured elsewhere. Call <c>MapHomeBlazeAgentMcp</c> on the application as well.
    /// </summary>
    public static IServiceCollection AddHomeBlazeAgents(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AgentServicesMarker)))
        {
            return services;
        }

        services.AddSingleton<AgentServicesMarker>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AgentTurnTokenRegistry>();
        services.TryAddSingleton<IAgentToolSource, HomeBlazeAgentToolSource>();
        services.TryAddSingleton<IAgentEndpointAddress, ServerAgentEndpointAddress>();
        services.TryAddSingleton<AgentServices>();

        // Post-configure so a callback set by WithHttpTransport is composed regardless of registration order.
        services.PostConfigure<HttpServerTransportOptions>(options =>
        {
            var previous = options.ConfigureSessionOptions;
            options.ConfigureSessionOptions = previous is null
                ? AgentMcpEndpoint.ConfigureSessionOptions
                : async (context, serverOptions, cancellationToken) =>
                {
                    await previous(context, serverOptions, cancellationToken);
                    await AgentMcpEndpoint.ConfigureSessionOptions(context, serverOptions, cancellationToken);
                };
        });
        return services;
    }
}

/// <summary>
/// Proves to <see cref="AgentMcpEndpoint.MapHomeBlazeAgentMcp"/> that <see cref="AgentServiceCollectionExtensions.AddHomeBlazeAgents"/> ran.
/// </summary>
internal sealed class AgentServicesMarker;
