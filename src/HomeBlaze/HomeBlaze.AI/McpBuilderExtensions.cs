using HomeBlaze.AI.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp.Extensions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI;

/// <summary>
/// The pieces a HomeBlaze MCP tool provider is built from, handed to satellite packages so they can
/// contribute tools without this package having to reference them.
/// </summary>
public readonly record struct McpToolProviderContext(
    IServiceProvider Services,
    Func<IInterceptorSubject> RootSubjectProvider,
    PathProviderBase PathProvider,
    bool IsReadOnly);

/// <summary>
/// Extension methods for registering HomeBlaze MCP tools with the MCP server builder.
/// </summary>
public static class McpBuilderExtensions
{
    /// <summary>
    /// Registers HomeBlaze-specific MCP tools including subject enrichment, type discovery,
    /// and method invocation, and sets <see cref="HomeBlazeMcpInstructions.ServerInstructions"/> as the server
    /// instructions unless the host already set them. Configuration is resolved lazily from the service provider.
    /// </summary>
    /// <remarks>
    /// Tools of other packages are added with
    /// <see cref="McpToolProviderServiceCollectionExtensions.AddHomeBlazeMcpToolProvider"/>, which serves both this
    /// server and the built-in agents.
    /// </remarks>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="isReadOnly">Whether operations are blocked and only queries are allowed.</param>
    public static IMcpServerBuilder WithHomeBlazeMcpTools(
        this IMcpServerBuilder builder,
        bool isReadOnly = true)
    {
        builder.Services.Configure<McpServerOptions>(options => options.ServerInstructions ??= HomeBlazeMcpInstructions.ServerInstructions);
        return builder.WithSubjectRegistryTools(
            HomeBlazeMcpConfigurationFactory.GetLoadedRoot,
            serviceProvider => HomeBlazeMcpConfigurationFactory.Create(
                serviceProvider,
                () => HomeBlazeMcpConfigurationFactory.GetLoadedRoot(serviceProvider),
                isReadOnly));
    }
}
