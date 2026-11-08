using HomeBlaze.Authorization;

namespace HomeBlaze.AI.Tests.Mcp;

/// <summary>The access of clients of the external MCP server, holding <see cref="Roles"/>.</summary>
internal sealed class TestMcpClientAccess(params string[] roles) : IMcpClientAccess
{
    public IReadOnlyCollection<string> Roles { get; set; } = roles;
}
