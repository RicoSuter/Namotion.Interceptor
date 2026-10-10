using System.Collections.Immutable;
using System.Text.Json;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The tools of one turn: the agent's allowed graph tools plus update_memory, each wrapped to enforce the
/// turn's tool-call budget and record the call. Both backends call tools only through this set.
/// </summary>
public sealed class AgentToolSet
{
    /// <summary>The graph tools an agent gets when it configures none.</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedTools =
        ["browse", "search", "get_property", "list_types", "list_methods", "get_property_history"];

    private const int PreviewLength = 2000;

    private readonly int _maxToolCalls;
    private readonly IProgress<AgentTurnUpdate>? _progress;
    private int _callCount;
    private ImmutableList<AgentToolCall> _calls = ImmutableList<AgentToolCall>.Empty;

    private AgentToolSet(int maxToolCalls, IProgress<AgentTurnUpdate>? progress)
    {
        _maxToolCalls = maxToolCalls;
        _progress = progress;
        Tools = [];
    }

    /// <summary>The wrapped tools.</summary>
    public IReadOnlyList<McpToolInfo> Tools { get; private set; }

    /// <summary>The calls made so far, in order.</summary>
    public IReadOnlyList<AgentToolCall> Calls => Volatile.Read(ref _calls);

    /// <summary>
    /// Whether tools built for these allowed names must run in read-only mode, i.e. neither set_property nor invoke_method is allowed.
    /// </summary>
    public static bool IsReadOnly(IReadOnlyCollection<string> allowedTools) =>
        !allowedTools.Contains("set_property") && !allowedTools.Contains("invoke_method");

    /// <summary>
    /// Creates the tool set of a turn.
    /// </summary>
    /// <param name="availableTools">All tools the configuration offers.</param>
    /// <param name="allowedTools">The names the agent may use; update_memory is always added.</param>
    /// <param name="memoryTool">The agent's update_memory tool.</param>
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
            .Append(memoryTool)
            .Select(toolSet.Wrap)
            .ToList();
        return toolSet;
    }

    private McpToolInfo Wrap(McpToolInfo tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = tool.InputSchema,
        Handler = async (input, cancellationToken) =>
        {
            var arguments = input.GetRawText();
            if (Interlocked.Increment(ref _callCount) > _maxToolCalls)
            {
                var message = $"The tool call budget of {_maxToolCalls} calls for this run is exhausted. Answer with what you have.";
                Record(new AgentToolCall(tool.Name, arguments, message, IsError: true));
                return new { error = message };
            }

            try
            {
                var result = await tool.Handler(input, cancellationToken);
                var element = result is null or string ? default : JsonSerializer.SerializeToElement(result);
                var isError = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("error", out _);
                var preview = result as string ?? (element.ValueKind == JsonValueKind.Undefined ? "null" : element.GetRawText());
                Record(new AgentToolCall(tool.Name, arguments, Truncate(preview), isError));
                return result;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Record(new AgentToolCall(tool.Name, arguments, Truncate(exception.Message), IsError: true));
                throw;
            }
        }
    };

    private void Record(AgentToolCall call)
    {
        ImmutableInterlocked.Update(ref _calls, calls => calls.Add(call));
        _progress?.Report(new AgentToolCallCompleted(call));
    }

    private static string Truncate(string text) => text.Length <= PreviewLength ? text : text[..PreviewLength] + "...";
}
