namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates McpToolInfo instances for all core and extension tools.
/// </summary>
public class McpToolFactory
{
    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly McpServerConfiguration _configuration;

    public McpToolFactory(IInterceptorSubject rootSubject, McpServerConfiguration configuration)
        : this(() => rootSubject, configuration)
    {
    }

    public McpToolFactory(Func<IInterceptorSubject> rootSubjectProvider, McpServerConfiguration configuration)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _configuration = configuration;
    }

    public IReadOnlyList<McpToolInfo> CreateTools()
    {
        var tools = new List<McpToolInfo>
        {
            new QueryTool(_rootSubjectProvider, _configuration).CreateTool(),
            new GetPropertyTool(_rootSubjectProvider, _configuration).CreateTool(),
            new SetPropertyTool(_rootSubjectProvider, _configuration).CreateTool(),
            new ListTypesTool(_configuration).CreateTool(),
        };

        foreach (var provider in _configuration.ToolProviders)
        {
            tools.AddRange(provider.GetTools());
        }

        return tools;
    }
}
