using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.AI.Mcp;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The tools of one turn: the agent's allowed graph tools plus update_memory, each wrapped to enforce the
/// turn's tool-call budget and record the call. Both backends call tools only through this set. Once the turn ends,
/// the set is closed and every call returns an error without running the tool.
/// </summary>
public sealed class AgentToolSet
{
    /// <summary>The graph tools a new agent allows by default. An empty allowed list allows no graph tools.</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedTools =
    [
        "browse", "search", HomeBlazeMarkdownSearchToolProvider.SearchMarkdownToolName, "get_property", "list_types", "list_methods",
        HomeBlazeMcpToolProvider.InvokeQueryToolName, "get_property_history"
    ];

    /// <summary>The graph tools that can change state: devices, notifications or pages.</summary>
    public static readonly IReadOnlyList<string> StateChangingTools =
    [
        HomeBlazeMcpToolProvider.InvokeOperationToolName,
        "set_property",
        .. HomeBlazeMarkdownToolProvider.ToolNames
    ];

    /// <summary>
    /// The permission in <see cref="LlmAgentBase.AllowedTools"/> that lets the Markdown tools and the operations the agent
    /// calls add, change, remove and move layout blocks of pages. It is no tool and needs a Markdown tool to be allowed.
    /// </summary>
    public const string EditPageLayoutPermission = "edit_page_layout";

    /// <summary>The tools whose page writes the permission to edit page layout widens.</summary>
    private static readonly IReadOnlyList<string> PageWritingTools =
        [.. HomeBlazeMarkdownToolProvider.ToolNames, HomeBlazeMcpToolProvider.InvokeOperationToolName];

    private const int PreviewLength = 2000;

    /// <summary>The error a call of a closed tool set returns.</summary>
    internal const string ClosedMessage = "The turn has ended; the tool was not run.";

    // Display only: previews are for the timeline and audit, never sent back to the model, so relaxed escaping
    // keeps non-ASCII characters readable and the enum converter matches what the model actually received.
    private static readonly JsonSerializerOptions PreviewSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly int _maxToolCalls;
    private readonly IProgress<AgentTurnUpdate>? _progress;
    private int _callCount;
    private volatile bool _isClosed;
    private ImmutableList<AgentToolCall> _calls = ImmutableList<AgentToolCall>.Empty;

    private AgentToolSet(int maxToolCalls, IProgress<AgentTurnUpdate>? progress)
    {
        _maxToolCalls = maxToolCalls;
        _progress = progress;
        Tools = [];
    }

    /// <summary>The wrapped tools.</summary>
    public IReadOnlyList<McpToolInfo> Tools { get; private set; }

    /// <summary>The calls made so far, in completion order.</summary>
    public IReadOnlyList<AgentToolCall> Calls => Volatile.Read(ref _calls);

    /// <summary>
    /// Closes the set when its turn ends. A call that started before keeps running; a call that starts later returns
    /// an error and is not recorded, for example one that passed the authentication of the agent MCP route just before
    /// the token was revoked.
    /// </summary>
    internal void Close() => _isClosed = true;

    /// <summary>
    /// Whether the allowed names grant the permission to edit page layout: <see cref="EditPageLayoutPermission"/> together
    /// with at least one Markdown tool.
    /// </summary>
    public static bool CanEditPageLayout(IReadOnlyCollection<string> allowedTools) =>
        allowedTools.Contains(EditPageLayoutPermission) && HasMarkdownTool(allowedTools);

    /// <summary>Whether at least one Markdown tool is allowed, which the permission to edit page layout needs.</summary>
    public static bool HasMarkdownTool(IReadOnlyCollection<string> allowedTools) =>
        HomeBlazeMarkdownToolProvider.ToolNames.Any(allowedTools.Contains);

    /// <summary>Whether a tool writes pages through the Markdown write operations, whose rules the permission widens.</summary>
    public static bool WritesPages(string toolName) => PageWritingTools.Contains(toolName);

    /// <summary>
    /// Whether tools built for these allowed names must run in read-only mode, i.e. none of the <see cref="StateChangingTools"/> is allowed.
    /// </summary>
    public static bool IsReadOnly(IReadOnlyCollection<string> allowedTools) =>
        !StateChangingTools.Any(allowedTools.Contains);

    /// <summary>
    /// Creates the tool set of a turn.
    /// </summary>
    /// <param name="availableTools">All tools the configuration offers.</param>
    /// <param name="allowedTools">The names the agent may use; update_memory is always added.</param>
    /// <param name="memoryTool">The agent's update_memory tool. Does not count against the call budget, so the
    /// agent can still save memory after exhausting it.</param>
    /// <param name="maxToolCalls">Calls after which further calls return an error.</param>
    /// <param name="progress">Receives an update per completed call.</param>
    public static AgentToolSet Create(
        IReadOnlyList<McpToolInfo> availableTools,
        IReadOnlyCollection<string> allowedTools,
        McpToolInfo memoryTool,
        int maxToolCalls,
        IProgress<AgentTurnUpdate>? progress)
    {
        var toolSet = new AgentToolSet(maxToolCalls, progress);
        toolSet.Tools = availableTools
            .Where(tool => allowedTools.Contains(tool.Name))
            .Select(tool => toolSet.Wrap(tool, countsAgainstBudget: true))
            .Append(toolSet.Wrap(memoryTool, countsAgainstBudget: false))
            .ToList();
        return toolSet;
    }

    private McpToolInfo Wrap(McpToolInfo tool, bool countsAgainstBudget) => new(tool)
    {
        Handler = async (input, cancellationToken) =>
        {
            if (_isClosed)
            {
                return new { error = ClosedMessage };
            }

            var arguments = Truncate(input.GetRawText());
            if (countsAgainstBudget && Interlocked.Increment(ref _callCount) > _maxToolCalls)
            {
                var message = $"The tool call budget of {_maxToolCalls} calls for this turn is exhausted. Answer with what you have.";
                Record(new AgentToolCall(tool.Name, arguments, message, IsError: true));
                return new { error = message };
            }

            object? result;
            try
            {
                result = await tool.Handler(input, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Record(new AgentToolCall(tool.Name, arguments, Truncate(exception.Message), IsError: true));
                throw;
            }

            var element = result is null or string ? default : JsonSerializer.SerializeToElement(result, PreviewSerializerOptions);
            var isError = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("error", out _);
            var preview = result as string ?? (element.ValueKind == JsonValueKind.Undefined ? "null" : element.GetRawText());
            Record(new AgentToolCall(tool.Name, arguments, Truncate(preview), isError));
            return result;
        }
    };

    /// <summary>
    /// Records a call of a backend tool, which the backend ran itself, so it shows in the timeline and counts against
    /// the turn's budget: graph tool calls beyond the budget then return an error. The backend tool call itself
    /// cannot be refused.
    /// </summary>
    /// <param name="name">The backend tool name, such as <c>web_search</c>.</param>
    /// <param name="arguments">The arguments as JSON, truncated for the preview.</param>
    /// <param name="result">The result, truncated for the preview.</param>
    /// <param name="isError">Whether the call failed.</param>
    public void RecordBackendCall(string name, string arguments, string result, bool isError)
    {
        Interlocked.Increment(ref _callCount);
        Record(new AgentToolCall(name, Truncate(arguments), Truncate(result), isError));
    }

    private void Record(AgentToolCall call)
    {
        ImmutableInterlocked.Update(ref _calls, calls => calls.Add(call));
        _progress?.Report(new AgentToolCallCompleted(call));
    }

    private static string Truncate(string text) => text.Length <= PreviewLength ? text : text[..PreviewLength] + "...";
}
