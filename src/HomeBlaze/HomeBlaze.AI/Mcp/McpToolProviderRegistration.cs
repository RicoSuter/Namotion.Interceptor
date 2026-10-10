using HomeBlaze.AI;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// A tool provider contributed by a satellite package, for example history. Every HomeBlaze tool
/// configuration (the MCP server and each agent) creates one instance from it.
/// </summary>
public sealed record McpToolProviderRegistration(Func<McpToolProviderContext, IMcpToolProvider> Create);

/// <summary>
/// Registers satellite MCP tool providers.
/// </summary>
public static class McpToolProviderServiceCollectionExtensions
{
    /// <summary>
    /// Adds a tool provider to every HomeBlaze tool configuration.
    /// </summary>
    public static IServiceCollection AddHomeBlazeMcpToolProvider(
        this IServiceCollection services, Func<McpToolProviderContext, IMcpToolProvider> create)
    {
        return services.AddSingleton(new McpToolProviderRegistration(create));
    }
}
