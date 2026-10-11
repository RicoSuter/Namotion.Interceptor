using HomeBlaze.AI.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp.Abstractions;
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
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="isReadOnly">Whether operations are blocked and only queries are allowed.</param>
    /// <param name="additionalToolProviders">
    /// Tools contributed by satellite packages, for example the history package's
    /// get_property_history. They are built inside the configuration factory because that is where the
    /// path provider and root subject exist, and taking them as a parameter is what lets this package
    /// stay independent of whatever happens to be installed. Providers can also be registered once with
    /// <see cref="McpToolProviderServiceCollectionExtensions.AddHomeBlazeMcpToolProvider"/> so the built-in
    /// agents get them too.
    /// </param>
    public static IMcpServerBuilder WithHomeBlazeMcpTools(
        this IMcpServerBuilder builder,
        bool isReadOnly = true,
        params Func<McpToolProviderContext, IMcpToolProvider>[] additionalToolProviders)
    {
        builder.Services.Configure<McpServerOptions>(options => options.ServerInstructions ??= HomeBlazeMcpInstructions.ServerInstructions);
        return builder.WithSubjectRegistryTools(
            HomeBlazeMcpConfigurationFactory.GetLoadedRoot,
            serviceProvider => HomeBlazeMcpConfigurationFactory.Create(
                serviceProvider,
                () => HomeBlazeMcpConfigurationFactory.GetLoadedRoot(serviceProvider),
                isReadOnly,
                additionalToolProviders));
    }
}
