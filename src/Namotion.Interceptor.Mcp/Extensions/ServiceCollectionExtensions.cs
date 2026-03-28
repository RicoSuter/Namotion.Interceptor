using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
            var tools = CreateFactory(request.Services!).CreateTools();
            return new ValueTask<ListToolsResult>(new ListToolsResult
            {
                Tools = tools.Select(tool => new Tool
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = tool.InputSchema
                }).ToList()
            });
        });

        builder.WithCallToolHandler(async (request, cancellationToken) =>
        {
            var tools = CreateFactory(request.Services!).CreateTools();
            var tool = tools.FirstOrDefault(t => t.Name == request.Params.Name);
            if (tool is null)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"Unknown tool: {request.Params.Name}" }]
                };
            }

            try
            {
                var input = request.Params.Arguments is not null
                    ? JsonSerializer.SerializeToElement(request.Params.Arguments)
                    : JsonSerializer.SerializeToElement(new { });
                var result = await tool.Handler(input, cancellationToken);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }]
                };
            }
            catch (Exception exception)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = exception.Message }]
                };
            }
        });

        return builder;
    }

    /// <summary>
    /// Registers subject registry tools with the MCP server builder.
    /// </summary>
    /// <param name="builder">The MCP server builder from <c>AddMcpServer()</c>.</param>
    /// <param name="rootSubject">The root interceptor subject to expose via MCP.</param>
    /// <param name="configuration">The server configuration controlling tool behavior.</param>
    /// <returns>The builder for further chaining.</returns>
    public static IMcpServerBuilder WithSubjectRegistryTools(
        this IMcpServerBuilder builder,
        IInterceptorSubject rootSubject,
        McpServerConfiguration configuration)
    {
        return builder.WithSubjectRegistryTools(_ => rootSubject, _ => configuration);
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
