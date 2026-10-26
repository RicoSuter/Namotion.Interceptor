using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Creates the HomeBlaze MCP tools for agents with <see cref="HomeBlazeMcpConfigurationFactory.Create"/>, deciding access
/// with the policy of the <see cref="BuiltInRoles.OperatorName"/> role.
/// </summary>
internal sealed class HomeBlazeAgentToolSource : IAgentToolSource
{
    private static readonly string[] OperatorRoleNames = [BuiltInRoles.OperatorName];

    private readonly IServiceProvider _serviceProvider;

    public HomeBlazeAgentToolSource(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IReadOnlyList<McpToolInfo> CreateTools(bool isReadOnly)
    {
        var rootSubjectProvider = () => HomeBlazeMcpConfigurationFactory.GetLoadedRoot(_serviceProvider);
        var accessPolicy = McpAuthorization.GetAccessPolicy(_serviceProvider, OperatorRoleNames);
        var configuration = HomeBlazeMcpConfigurationFactory.Create(_serviceProvider, rootSubjectProvider, isReadOnly, accessPolicy);
        return new McpToolFactory(rootSubjectProvider, configuration).CreateTools();
    }
}
