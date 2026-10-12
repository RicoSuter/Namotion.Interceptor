using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents.Anthropic;

/// <summary>
/// Builds the chat options of an Anthropic turn.
/// </summary>
internal static class AnthropicChatOptions
{
    /// <summary>
    /// Creates the options for one model request of a turn. Any effort enables adaptive thinking and is sent as
    /// <c>output_config.effort</c>; <paramref name="maxOutputTokens"/> is sent unchanged as <c>max_tokens</c>, and
    /// the request enables automatic prompt caching.
    /// </summary>
    /// <param name="model">The model id.</param>
    /// <param name="maxOutputTokens">The output token limit, including thinking tokens.</param>
    /// <param name="effort">The effort, or null for the model's default.</param>
    /// <param name="tools">
    /// The tools the model may call; the server tools of the backend tools are added per request by
    /// <see cref="WithBackendToolUses"/>.
    /// </param>
    public static ChatOptions Create(string model, int maxOutputTokens, AgentEffort? effort, IReadOnlyList<McpToolInfo> tools)
    {
        var aiTools = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            aiTools.Add(tool.ToAIFunction());
        }

        // An explicit MaxOutputTokens stops the client from raising max_tokens above the configured limit to fit a thinking budget.
        var options = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = maxOutputTokens,
            Tools = aiTools
        };

        if (effort is { } value)
        {
            // ReasoningEffort has no max level: ExtraHigh makes the client add adaptive thinking, and the client keeps
            // the max effort of the raw parameters below.
            options.Reasoning = new ReasoningOptions
            {
                Effort = value switch
                {
                    AgentEffort.Low => ReasoningEffort.Low,
                    AgentEffort.Medium => ReasoningEffort.Medium,
                    AgentEffort.High => ReasoningEffort.High,
                    AgentEffort.ExtraHigh or AgentEffort.Max => ReasoningEffort.ExtraHigh,
                    _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, null)
                }
            };
        }

        // Automatic prompt caching marks the end of each request, so the tool rounds of a turn and the next chat turn
        // read the shared prefix from the cache. With raw parameters the client takes model and max_tokens from them
        // instead of the options, and appends the chat messages to the empty message list.
        var outputConfig = effort == AgentEffort.Max ? new OutputConfig { Effort = Effort.Max } : null;
        options.RawRepresentationFactory = _ => new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxOutputTokens,
            Messages = [],
            CacheControl = new CacheControlEphemeral(),
            OutputConfig = outputConfig
        };

        return options;
    }

    /// <summary>
    /// Copies the options of a turn for one model request with the use limit of each server tool set to
    /// <paramref name="maxBackendToolUses"/>, at least 1.
    /// </summary>
    /// <param name="options">The options of the turn, created by <see cref="Create"/>.</param>
    /// <param name="backendTools">The backend tools of the turn.</param>
    /// <param name="maxBackendToolUses">The use limit of each server tool for this request.</param>
    public static ChatOptions WithBackendToolUses(ChatOptions options, IReadOnlyList<string> backendTools, int maxBackendToolUses)
    {
        var requestOptions = options.Clone();
        var aiTools = new List<AITool>(options.Tools?.Count ?? 0);
        aiTools.AddRange(options.Tools?.OfType<AIFunctionDeclaration>() ?? []);
        AddServerTools(aiTools, backendTools, maxBackendToolUses);
        requestOptions.Tools = aiTools;
        return requestOptions;
    }

    private static void AddServerTools(List<AITool> aiTools, IReadOnlyList<string> backendTools, int maxBackendToolUses)
    {
        long maxUses = Math.Max(maxBackendToolUses, 1);
        if (backendTools.Contains(AgentBackendTool.WebSearch.Name))
        {
            aiTools.Add(new ToolUnion(new WebSearchTool20260209 { MaxUses = maxUses }).AsAITool());
        }

        if (backendTools.Contains(AgentBackendTool.WebFetch.Name))
        {
            aiTools.Add(new ToolUnion(new WebFetchTool20260209 { MaxUses = maxUses }).AsAITool());
        }
    }
}
