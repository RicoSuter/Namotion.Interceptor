namespace HomeBlaze.Authorization;

/// <summary>
/// The access of the clients of the external MCP endpoint: the roles of the built-in user
/// <see cref="NameRules.McpUserName"/>. Thread-safe.
/// </summary>
public interface IMcpClientAccess
{
    /// <summary>The names of the roles of the built-in user <see cref="NameRules.McpUserName"/>; empty when it does not exist.</summary>
    IReadOnlyCollection<string> Roles { get; }
}
