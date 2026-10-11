using HomeBlaze.AI.Agents;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// One allowed-tools toggle of the agent edit form.
/// </summary>
/// <param name="Name">The tool name stored in <see cref="LlmAgentBase.AllowedTools"/>.</param>
/// <param name="Description">The tool description, null when unknown.</param>
/// <param name="IsOffered">Whether the installation currently offers the tool.</param>
/// <param name="CanChangeState">Whether the tool can change devices, including sending notifications.</param>
public sealed record AgentToolOption(string Name, string? Description, bool IsOffered, bool CanChangeState);

/// <summary>
/// Builds the allowed-tools toggles of the agent edit form.
/// </summary>
public static class AgentToolOptions
{
    /// <summary>The tool names shown when the offered tools cannot be listed.</summary>
    public static IReadOnlyList<string> FallbackToolNames { get; } =
        ["browse", "search", "get_property", "list_types", "list_methods", "get_property_history", "invoke_method", "set_property"];

    private const string MemoryToolName = "update_memory";

    /// <summary>
    /// Lists the tools of <paramref name="toolSource"/>, or <see cref="FallbackToolNames"/> when listing fails,
    /// for example while the root subject is still loading.
    /// </summary>
    public static IReadOnlyList<AgentToolOption> Create(IAgentToolSource toolSource, IEnumerable<string> allowedTools)
    {
        IReadOnlyList<McpToolInfo>? offeredTools;
        try
        {
            offeredTools = toolSource.CreateTools(isReadOnly: false);
        }
        catch (Exception)
        {
            offeredTools = null;
        }

        return Create(offeredTools, allowedTools);
    }

    /// <summary>
    /// Creates a toggle per offered tool, read-only tools first, followed by a toggle per allowed name that is not
    /// offered, so it can be turned off. update_memory is excluded because it is always available.
    /// </summary>
    /// <param name="offeredTools">The offered tools, null to use <see cref="FallbackToolNames"/>.</param>
    /// <param name="allowedTools">The configured allowed tool names.</param>
    public static IReadOnlyList<AgentToolOption> Create(IReadOnlyList<McpToolInfo>? offeredTools, IEnumerable<string> allowedTools)
    {
        var offered = offeredTools?.Select(tool => (tool.Name, (string?)tool.Description))
            ?? FallbackToolNames.Select(name => (name, (string?)null));

        var options = new List<AgentToolOption>();
        var names = new HashSet<string>(StringComparer.Ordinal) { MemoryToolName };
        foreach (var (name, description) in offered)
        {
            if (names.Add(name))
            {
                options.Add(new AgentToolOption(name, description, IsOffered: true, CanChangeState(name)));
            }
        }

        var sorted = options.OrderBy(option => option.CanChangeState).ToList();
        foreach (var name in allowedTools)
        {
            if (names.Add(name))
            {
                sorted.Add(new AgentToolOption(name, "Not offered by this installation.", IsOffered: false, CanChangeState(name)));
            }
        }

        return sorted;
    }

    private static bool CanChangeState(string name) => !AgentToolSet.IsReadOnly([name]);
}
