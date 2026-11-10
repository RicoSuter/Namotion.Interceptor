using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Extensions;

namespace HomeBlaze.AI;

/// <summary>
/// The pieces a HomeBlaze MCP tool provider is built from, handed to satellite packages so they can
/// contribute tools without this package having to reference them.
/// </summary>
/// <param name="Services">The services of the tools.</param>
/// <param name="RootSubjectProvider">Resolves the root subject when a tool runs.</param>
/// <param name="Configuration">
/// The configuration the provider is added to, whose access policy decides per member and action; resolve paths with
/// its <c>TryGetReadableProperty</c> and <c>TryGetReadableSubject</c> and check actions with its <c>IsAllowed</c>.
/// </param>
public readonly record struct McpToolProviderContext(
    IServiceProvider Services,
    Func<IInterceptorSubject> RootSubjectProvider,
    McpServerConfiguration Configuration);

/// <summary>
/// Extension methods for registering HomeBlaze MCP tools with the MCP server builder.
/// </summary>
public static class McpBuilderExtensions
{
    /// <summary>
    /// Registers HomeBlaze-specific MCP tools including subject enrichment, type discovery,
    /// and method invocation, and sets <see cref="HomeBlazeMcpInstructions.ServerInstructions"/> as the server instructions
    /// unless the host already set them. Configuration is resolved lazily from the service provider, per request.
    /// The tools decide access with the policy of the roles of the registered <see cref="IMcpClientAccess"/> from the
    /// <see cref="AuthorizationService"/>, so the system rules of non-human principals apply; without one they allow nothing.
    /// </summary>
    /// <remarks>
    /// Tools of other packages are added with
    /// <see cref="McpToolProviderServiceCollectionExtensions.AddHomeBlazeMcpToolProvider"/>, which serves both this
    /// server and the built-in agents.
    /// </remarks>
    /// <param name="builder">The MCP server builder.</param>
    public static IMcpServerBuilder WithHomeBlazeMcpTools(this IMcpServerBuilder builder)
    {
        builder.Services.Configure<McpServerOptions>(options => options.ServerInstructions ??= HomeBlazeMcpInstructions.ServerInstructions);
        builder.Services.AddHomeBlazeAuthorization(McpAuthorization.PathProvider);
        return builder.WithSubjectRegistryTools(
            HomeBlazeMcpConfigurationFactory.GetLoadedRoot,
            serviceProvider => CreateServerConfiguration(
                serviceProvider, () => HomeBlazeMcpConfigurationFactory.GetLoadedRoot(serviceProvider)));
    }

    /// <summary>
    /// Creates the configuration of the tools of the external MCP server, which decide access with the policy of the
    /// roles of the registered <see cref="IMcpClientAccess"/>; <see cref="AuthorizationPolicy.DenyAll"/> when none is
    /// registered.
    /// </summary>
    internal static McpServerConfiguration CreateServerConfiguration(IServiceProvider serviceProvider, Func<IInterceptorSubject> rootSubjectProvider)
    {
        var access = serviceProvider.GetService<IMcpClientAccess>();
        var accessPolicy = access is null
            ? new AuthorizationMcpAccessPolicy(() => AuthorizationPolicy.DenyAll)
            : McpAuthorization.GetAccessPolicy(serviceProvider.GetRequiredService<AuthorizationService>(), access.Roles);
        return HomeBlazeMcpConfigurationFactory.Create(serviceProvider, rootSubjectProvider, accessPolicy);
    }
}
