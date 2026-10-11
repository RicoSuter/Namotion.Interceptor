using HomeBlaze.AI.Mcp;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Creates the HomeBlaze MCP tools for agents, with the configuration of the MCP server except that configuration
/// properties cannot be written.
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
        var configuration = HomeBlazeMcpConfigurationFactory.CreateForAgents(_serviceProvider, rootSubjectProvider, isReadOnly);
        return new McpToolFactory(rootSubjectProvider, configuration).CreateTools();
    }
}
