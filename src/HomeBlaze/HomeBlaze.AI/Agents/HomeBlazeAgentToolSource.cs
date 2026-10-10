using HomeBlaze.AI.Mcp;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Creates the HomeBlaze MCP tools for agents, with the same configuration as the MCP server.
/// </summary>
public sealed class HomeBlazeAgentToolSource : IAgentToolSource
{
    private readonly IServiceProvider _serviceProvider;

    public HomeBlazeAgentToolSource(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IReadOnlyList<McpToolInfo> CreateTools(bool isReadOnly)
    {
        var rootSubjectProvider = () => HomeBlazeMcpConfigurationFactory.GetLoadedRoot(_serviceProvider);
        var configuration = HomeBlazeMcpConfigurationFactory.Create(_serviceProvider, rootSubjectProvider, isReadOnly);
        return new McpToolFactory(rootSubjectProvider, configuration).CreateTools();
    }
}
