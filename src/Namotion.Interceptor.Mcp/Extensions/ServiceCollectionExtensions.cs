using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Extensions;

/// <summary>
/// Extension methods for registering the MCP subject tools with the MCP server builder.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers subject registry tools with the MCP server builder.
    /// Tools are listed and called via custom MCP handlers that lazily resolve configuration from the service provider.
    /// </summary>
    /// <param name="builder">The MCP server builder from <c>AddMcpServer()</c>.</param>
    /// <param name="rootSubjectProvider">A function that resolves the root subject from the service provider.</param>
    /// <param name="configurationFactory">A function that creates the server configuration from the service provider.</param>
    /// <returns>The builder for further chaining.</returns>
    public static IMcpServerBuilder WithSubjectRegistryTools(
        this IMcpServerBuilder builder,
        Func<IServiceProvider, IInterceptorSubject> rootSubjectProvider,
        Func<IServiceProvider, McpServerConfiguration> configurationFactory)
    {
        McpToolFactory CreateFactory(IServiceProvider services)
        {
            return new McpToolFactory(
                () => rootSubjectProvider(services),
                configurationFactory(services));
        }

        builder.WithListToolsHandler((request, cancellationToken) =>
        {
            try
            {
                var tools = CreateFactory(request.Services!).CreateTools();
                return new ValueTask<ListToolsResult>(McpToolDispatcher.ListTools(tools));
            }
            catch (Exception exception)
            {
                var logger = request.Services?.GetService<ILogger<McpToolFactory>>();
                logger?.LogWarning(exception, "MCP ListTools failed.");
                throw;
            }
        });

        builder.WithCallToolHandler((request, cancellationToken) =>
        {
            IReadOnlyList<McpToolInfo> tools;
            try
            {
                tools = CreateFactory(request.Services!).CreateTools();
            }
            catch (Exception exception)
            {
                var logger = request.Services?.GetService<ILogger<McpToolFactory>>();
                logger?.LogWarning(exception, "MCP tool '{ToolName}' failed.", request.Params!.Name);
                return new ValueTask<CallToolResult>(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = exception.Message }]
                });
            }

            return McpToolDispatcher.CallToolAsync(
                tools, request.Params!, request.Services?.GetService<ILogger<McpToolFactory>>(), cancellationToken);
        });

        return builder;
    }

    /// <summary>
    /// Registers subject registry tools with the MCP server builder using lazy root subject resolution.
    /// </summary>
    /// <param name="builder">The MCP server builder from <c>AddMcpServer()</c>.</param>
    /// <param name="rootSubjectProvider">A function that resolves the root subject at tool invocation time.</param>
    /// <param name="configuration">The server configuration controlling tool behavior.</param>
    /// <returns>The builder for further chaining.</returns>
    public static IMcpServerBuilder WithSubjectRegistryTools(
        this IMcpServerBuilder builder,
        Func<IInterceptorSubject> rootSubjectProvider,
        McpServerConfiguration configuration)
    {
        return builder.WithSubjectRegistryTools(_ => rootSubjectProvider(), _ => configuration);
    }
}
