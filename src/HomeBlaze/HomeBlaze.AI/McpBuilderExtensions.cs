using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Extensions;

namespace HomeBlaze.AI;

/// <summary>
/// Extension methods for registering HomeBlaze MCP tools with the MCP server builder.
/// </summary>
public static class McpBuilderExtensions
{
    /// <summary>
    /// Registers HomeBlaze-specific MCP tools including subject enrichment, type discovery,
    /// and method invocation. Configuration is resolved lazily from the service provider.
    /// </summary>
    public static IMcpServerBuilder WithHomeBlazeMcpTools(
        this IMcpServerBuilder builder,
        bool isReadOnly = true)
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

                return new McpServerConfiguration
                {
                    PathProvider = pathProvider,
                    PathPrefix = "/",
                    ExcludeTypes = excludeTypes,
                    SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher(typeProviders, isReadOnly) },
                    TypeProviders = typeProviders,
                    ToolProviders =
                    {
                        new HomeBlazeMcpToolProvider(
                            () => sp.GetRequiredService<RootManager>().Root!,
                            pathProvider, sp,
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<HomeBlazeMcpToolProvider>(),
                            isReadOnly)
                    },
                    IsReadOnly = isReadOnly
                };
            });
    }
}
