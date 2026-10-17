namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Who an operation or a query is meant for, which decides where it is listed and can be run from.
/// </summary>
[Flags]
public enum MethodAudience
{
    /// <summary>
    /// People, through user interfaces such as the browser.
    /// </summary>
    People = 1,

    /// <summary>
    /// Agents, through programmatic agent interfaces such as MCP.
    /// </summary>
    Agents = 2,

    /// <summary>
    /// People and agents.
    /// </summary>
    All = People | Agents
}
