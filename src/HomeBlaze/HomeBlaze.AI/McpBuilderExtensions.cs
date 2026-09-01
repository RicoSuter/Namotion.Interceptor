using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
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
    /// and method invocation. Configuration is resolved lazily from the service provider.
    /// </summary>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="isReadOnly">Whether operations are blocked and only queries are allowed.</param>
    /// <param name="additionalToolProviders">
    /// Tools contributed by satellite packages, for example the history package's
    /// get_property_history. They are built inside the configuration factory because that is where the
    /// path provider and root subject exist, and taking them as a parameter is what lets this package
    /// stay independent of whatever happens to be installed.
    /// </param>
    public static IMcpServerBuilder WithHomeBlazeMcpTools(
        this IMcpServerBuilder builder,
        bool isReadOnly = true,
        params Func<McpToolProviderContext, IMcpToolProvider>[] additionalToolProviders)
    {
        return builder.WithSubjectRegistryTools(
            sp => sp.GetRequiredService<RootManager>().Root!,
            sp =>
            {
                var typeRegistry = sp.GetRequiredService<SubjectTypeRegistry>();
                var pathProvider = new StateAttributePathProvider();
                var typeProviders = new IMcpTypeProvider[]
                {
                    new SubjectAbstractionTypeProvider(),
                    new SubjectTypeRegistryTypeProvider(typeRegistry)
                };

                var excludeTypes = typeRegistry.RegisteredTypes
                    .Where(type => type.GetCustomAttributes(typeof(ExcludeFromBrowsingAttribute), true).Length > 0)
                    .ToArray();

                var rootSubjectProvider = () => sp.GetRequiredService<RootManager>().Root!;
                var configuration = new McpServerConfiguration
                {
                    PathProvider = pathProvider,
                    PathPrefix = "/",
                    ExcludeTypes = excludeTypes,
                    SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher(typeProviders, isReadOnly) },
                    TypeProviders = typeProviders,
                    ToolProviders =
                    {
                        new HomeBlazeMcpToolProvider(
                            rootSubjectProvider,
                            pathProvider, sp,
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<HomeBlazeMcpToolProvider>(),
                            isReadOnly)
                    },
                    IsReadOnly = isReadOnly
                };

                var context = new McpToolProviderContext(sp, rootSubjectProvider, pathProvider, isReadOnly);
                foreach (var create in additionalToolProviders)
                {
                    configuration.ToolProviders.Add(create(context));
                }

                return configuration;
            });
    }
}
