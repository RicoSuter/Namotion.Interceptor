using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents.Anthropic;

/// <summary>
/// Builds the chat options of an Anthropic turn.
/// </summary>
public static class AnthropicChatOptions
{
    /// <summary>
    /// Creates the options for one model request of a turn. Any effort enables adaptive thinking and is sent as
    /// <c>output_config.effort</c>; <paramref name="maxOutputTokens"/> is sent unchanged as <c>max_tokens</c>.
    /// </summary>
    /// <param name="model">The model id.</param>
    /// <param name="maxOutputTokens">The output token limit, including thinking tokens.</param>
    /// <param name="effort">The effort, or null for the model default without thinking.</param>
    /// <param name="tools">The tools the model may call.</param>
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
            // the raw effort. With raw parameters the client takes model and max_tokens from them, not from the options.
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

            if (value == AgentEffort.Max)
            {
                options.RawRepresentationFactory = _ => new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = maxOutputTokens,
                    Messages = [],
                    OutputConfig = new OutputConfig { Effort = Effort.Max }
                };
            }
        }

        return options;
    }
}
