using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// MCP server that exposes the subject registry via the MCP protocol.
/// Creates transport-agnostic <see cref="McpToolDescriptor"/> instances from the subject tree
/// and provides methods to convert them into <see cref="AIFunction"/> or <see cref="McpServerTool"/>
/// for use with the ModelContextProtocol SDK.
/// </summary>
public class McpSubjectServer
{
    private readonly McpToolFactory _toolFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpSubjectServer"/> class.
    /// </summary>
    /// <param name="rootSubject">The root interceptor subject to expose via MCP.</param>
    /// <param name="configuration">The server configuration controlling tool behavior.</param>
    public McpSubjectServer(IInterceptorSubject rootSubject, McpServerConfiguration configuration)
    {
        _toolFactory = new McpToolFactory(rootSubject, configuration);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="McpSubjectServer"/> class with lazy root subject resolution.
    /// Use this when the root subject is not available at registration time (e.g., loaded asynchronously at startup).
    /// </summary>
    /// <param name="rootSubjectProvider">A function that resolves the root subject at tool invocation time.</param>
    /// <param name="configuration">The server configuration controlling tool behavior.</param>
    public McpSubjectServer(Func<IInterceptorSubject> rootSubjectProvider, McpServerConfiguration configuration)
    {
        _toolFactory = new McpToolFactory(rootSubjectProvider, configuration);
    }

    /// <summary>
    /// Gets the transport-agnostic tool descriptors for all registered tools.
    /// </summary>
    /// <returns>A read-only list of tool descriptors.</returns>
    public IReadOnlyList<McpToolDescriptor> GetToolDescriptors() => _toolFactory.CreateTools();

    /// <summary>
    /// Creates <see cref="AIFunction"/> instances from the tool descriptors.
    /// Useful for consumers that want to integrate with AI function abstractions
    /// without depending on the MCP protocol directly.
    /// </summary>
    /// <returns>A list of AI functions wrapping the tool descriptors.</returns>
    public IReadOnlyList<AIFunction> CreateAIFunctions()
    {
        return GetToolDescriptors()
            .Select(descriptor => (AIFunction)new McpToolAIFunction(descriptor))
            .ToList();
    }

    /// <summary>
    /// Creates <see cref="McpServerTool"/> instances for use with the ModelContextProtocol SDK.
    /// Each tool descriptor is wrapped as an <see cref="AIFunction"/> and then registered
    /// as an <see cref="McpServerTool"/>.
    /// </summary>
    /// <returns>A list of MCP server tools ready for SDK registration.</returns>
    public IReadOnlyList<McpServerTool> CreateMcpServerTools()
    {
        return GetToolDescriptors()
            .Select(descriptor => McpServerTool.Create(new McpToolAIFunction(descriptor)))
            .ToList();
    }
}
