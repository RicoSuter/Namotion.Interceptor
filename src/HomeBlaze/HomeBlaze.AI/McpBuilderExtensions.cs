using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Extensions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI;

/// <summary>
/// The pieces a HomeBlaze MCP tool provider is built from, handed to satellite packages so they can
/// contribute tools without this package having to reference them.
/// </summary>
/// <param name="Services">The services of the tools.</param>
/// <param name="RootSubjectProvider">Resolves the root subject when a tool runs.</param>
/// <param name="PathProvider">Resolves and writes the paths of the tools.</param>
/// <param name="IsReadOnly">Whether the tools may not change anything.</param>
/// <param name="Configuration">
/// The configuration the provider is added to, whose access policy decides per member and action; resolve paths with
/// its <c>TryGetReadableProperty</c> and <c>TryGetReadableSubject</c> and check actions with its <c>IsAllowed</c>.
/// </param>
public readonly record struct McpToolProviderContext(
    IServiceProvider Services,
    Func<IInterceptorSubject> RootSubjectProvider,
    PathProviderBase PathProvider,
    bool IsReadOnly,
    McpServerConfiguration Configuration);

/// <summary>
/// Extension methods for registering HomeBlaze MCP tools with the MCP server builder.
/// </summary>
public static class McpBuilderExtensions
{
    private static readonly string[] EverythingRoleNames = [BuiltInRoles.EverythingName];

    /// <summary>
    /// Registers HomeBlaze-specific MCP tools including subject enrichment, type discovery,
    /// and method invocation, and sets <see cref="HomeBlazeMcpInstructions.GetServerInstructions"/> for
    /// <paramref name="isReadOnly"/> as the server instructions unless the host already set them. Configuration is resolved lazily from the service provider.
    /// The tools decide access with the policy of the <see cref="BuiltInRoles.EverythingName"/> role from the
    /// <see cref="AuthorizationService"/>, so the system rules apply.
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
        builder.Services.Configure<McpServerOptions>(options => options.ServerInstructions ??= HomeBlazeMcpInstructions.GetServerInstructions(isReadOnly));
        builder.Services.AddHomeBlazeAuthorization(McpAuthorization.PathProvider);
        return builder.WithSubjectRegistryTools(
            HomeBlazeMcpConfigurationFactory.GetLoadedRoot,
            serviceProvider => CreateServerConfiguration(
                serviceProvider, () => HomeBlazeMcpConfigurationFactory.GetLoadedRoot(serviceProvider), isReadOnly));
    }

    /// <summary>
    /// Creates the configuration of the tools of the external MCP server, which decide access with the policy of the
    /// <see cref="BuiltInRoles.EverythingName"/> role from the <see cref="AuthorizationService"/>.
    /// </summary>
    internal static McpServerConfiguration CreateServerConfiguration(
        IServiceProvider serviceProvider, Func<IInterceptorSubject> rootSubjectProvider, bool isReadOnly)
        => HomeBlazeMcpConfigurationFactory.Create(
            serviceProvider, rootSubjectProvider, isReadOnly, McpAuthorization.GetAccessPolicy(serviceProvider, EverythingRoleNames));
}
