using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// One allowed-tools toggle of the agent edit form.
/// </summary>
/// <param name="Name">The tool name stored in <see cref="LlmAgentBase.AllowedTools"/>.</param>
/// <param name="Description">A short summary for known tools, else the tool description; null when unknown.</param>
/// <param name="IsOffered">Whether the installation currently offers the tool.</param>
/// <param name="CanChangeState">Whether the tool can change state: devices, notifications or pages.</param>
/// <param name="LoadsWebContent">Whether the tool is a backend tool that brings untrusted web content into the turn.</param>
/// <param name="ChangesPages">Whether the tool changes Markdown pages and documents rather than devices.</param>
/// <param name="ChangesPageLayout">Whether the entry is the permission to edit page layout, which is no tool and needs a
/// Markdown tool.</param>
internal sealed record AgentToolOption(
    string Name,
    string? Description,
    bool IsOffered,
    bool CanChangeState,
    bool LoadsWebContent = false,
    bool ChangesPages = false,
    bool ChangesPageLayout = false);

/// <summary>
/// Builds the allowed-tools toggles of the agent edit form.
/// </summary>
internal static class AgentToolOptions
{
    /// <summary>The tool names shown when the offered tools cannot be listed.</summary>
    public static IReadOnlyList<string> FallbackToolNames { get; } =
        [.. AgentToolSet.DefaultAllowedTools, .. AgentToolSet.StateChangingTools];

    // Operator-facing summaries; the MCP descriptions are written for the model and are long.
    private static readonly Dictionary<string, string> Summaries = new(StringComparer.Ordinal)
    {
        ["browse"] = "Browses the subject tree and reads the properties of subjects.",
        ["search"] = "Finds subjects by type, title or path.",
        ["get_property"] = "Reads a single property value.",
        ["list_types"] = "Lists the known types and interfaces, so subjects can be found by capability.",
        ["list_methods"] = "Lists the operations and queries a subject offers.",
        [HomeBlazeMcpToolProvider.InvokeQueryToolName] = "Calls read-only query methods, for example to read Markdown pages.",
        ["get_property_history"] = "Reads the recorded history of property values.",
        [HomeBlazeMcpToolProvider.InvokeOperationToolName] = "Runs operations on subjects, such as switching a device or sending a notification.",
        ["set_property"] = "Writes property values of subjects.",
        [HomeBlazeMarkdownToolProvider.EditMarkdownToolName] = "Replaces a passage of a Markdown page or document.",
        [HomeBlazeMarkdownToolProvider.WriteMarkdownToolName] = "Replaces the whole text of a Markdown page or document.",
        [HomeBlazeMarkdownToolProvider.CreateMarkdownToolName] = "Creates a Markdown page or document in a folder."
    };

    private const string PageLayoutSummary =
        "Lets the Markdown tools add, change, remove and move layout blocks of pages: cards, tabs, layouts, style blocks, " +
        "value widgets and widgets of existing subjects. Needs a Markdown tool.";

    private static readonly HashSet<string> PageTools = new(HomeBlazeMarkdownToolProvider.ToolNames, StringComparer.Ordinal);

    /// <summary>
    /// Lists the tools of <paramref name="toolSource"/>, or <see cref="FallbackToolNames"/> when listing fails, followed
    /// by the backend tools of the agent type.
    /// </summary>
    public static IReadOnlyList<AgentToolOption> Create(
        IAgentToolSource toolSource, IEnumerable<string> allowedTools, IReadOnlyList<AgentBackendTool>? backendTools = null)
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

        return Create(offeredTools, allowedTools, backendTools);
    }

    /// <summary>
    /// Creates a toggle per offered tool, read-only tools first, with the permission to edit page layout right after the
    /// Markdown tools when one is offered, then a toggle per backend tool of the agent type,
    /// followed by a toggle per allowed name that is not offered, so it can be turned off. update_memory is excluded
    /// because it is always available. Known tools are described by a short summary instead of their tool description.
    /// </summary>
    /// <param name="offeredTools">The offered tools, null to use <see cref="FallbackToolNames"/>.</param>
    /// <param name="allowedTools">The configured allowed tool names.</param>
    /// <param name="backendTools">The backend tools the agent type supports.</param>
    public static IReadOnlyList<AgentToolOption> Create(
        IReadOnlyList<McpToolInfo>? offeredTools, IEnumerable<string> allowedTools, IReadOnlyList<AgentBackendTool>? backendTools = null)
    {
        var offered = offeredTools?.Select(tool => (tool.Name, (string?)tool.Description))
            ?? FallbackToolNames.Select(name => (name, (string?)null));

        var options = new List<AgentToolOption>();
        var names = new HashSet<string>(StringComparer.Ordinal) { AgentMemory.ToolName };
        foreach (var (name, description) in offered)
        {
            if (names.Add(name))
            {
                options.Add(new AgentToolOption(
                    name, Summaries.GetValueOrDefault(name) ?? description, IsOffered: true, CanChangeState(name), ChangesPages: PageTools.Contains(name)));
            }
        }

        var sorted = options.OrderBy(option => option.CanChangeState).ToList();
        InsertPageLayoutOption(sorted, names);
        foreach (var backendTool in backendTools ?? [])
        {
            if (names.Add(backendTool.Name))
            {
                sorted.Add(new AgentToolOption(backendTool.Name, backendTool.Summary, IsOffered: true, CanChangeState: false, backendTool.LoadsWebContent));
            }
        }

        foreach (var name in allowedTools)
        {
            if (names.Add(name))
            {
                sorted.Add(CreateNotOfferedOption(name));
            }
        }

        return sorted;
    }

    // Right after the Markdown tools, whose page writes it widens, when one of them is offered.
    private static void InsertPageLayoutOption(List<AgentToolOption> sorted, HashSet<string> names)
    {
        var lastPageToolIndex = sorted.FindLastIndex(option => option.ChangesPages);
        if (lastPageToolIndex >= 0 && names.Add(AgentToolSet.EditPageLayoutPermission))
        {
            sorted.Insert(lastPageToolIndex + 1, new AgentToolOption(
                AgentToolSet.EditPageLayoutPermission, PageLayoutSummary, IsOffered: true, CanChangeState: true, ChangesPageLayout: true));
        }
    }

    private static AgentToolOption CreateNotOfferedOption(string name)
    {
        var isPageLayout = name == AgentToolSet.EditPageLayoutPermission;
        return new AgentToolOption(
            name, "Not offered by this installation.", IsOffered: false, isPageLayout || CanChangeState(name),
            ChangesPages: PageTools.Contains(name), ChangesPageLayout: isPageLayout);
    }

    private static bool CanChangeState(string name) => !AgentToolSet.IsReadOnly([name]);
}
