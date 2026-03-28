using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.Mcp.Extensions;

/// <summary>
/// Extension methods for registering the MCP subject server with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds an <see cref="McpSubjectServer"/> as a singleton and registers its tools
    /// with the ModelContextProtocol SDK server builder.
    /// </summary>
    /// <param name="builder">The MCP server builder from <c>AddMcpServer()</c>.</param>
    /// <param name="rootSubject">The root interceptor subject to expose via MCP.</param>
    /// <param name="configuration">The server configuration controlling tool behavior.</param>
    /// <returns>The builder for further chaining.</returns>
    public static IMcpServerBuilder WithSubjectServerTools(
        this IMcpServerBuilder builder,
        IInterceptorSubject rootSubject,
        McpServerConfiguration configuration)
    {
        var server = new McpSubjectServer(rootSubject, configuration);
        builder.Services.AddSingleton(server);
        builder.WithTools(server.CreateMcpServerTools());
        return builder;
    }

    /// <summary>
    /// Adds an <see cref="McpSubjectServer"/> as a singleton with lazy root subject resolution
    /// and registers its tools with the ModelContextProtocol SDK server builder.
    /// Use this overload when the root subject is not available at registration time.
    /// </summary>
    /// <param name="builder">The MCP server builder from <c>AddMcpServer()</c>.</param>
    /// <param name="rootSubjectProvider">A function that resolves the root subject at tool invocation time.</param>
    /// <param name="configuration">The server configuration controlling tool behavior.</param>
    /// <returns>The builder for further chaining.</returns>
    public static IMcpServerBuilder WithSubjectServerTools(
        this IMcpServerBuilder builder,
        Func<IInterceptorSubject> rootSubjectProvider,
        McpServerConfiguration configuration)
    {
        var server = new McpSubjectServer(rootSubjectProvider, configuration);
        builder.Services.AddSingleton(server);
        builder.WithTools(server.CreateMcpServerTools());
        return builder;
    }
}
