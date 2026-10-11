using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.AI.Agents.Anthropic;

/// <summary>
/// An agent that calls the Anthropic API with an API key and runs the tool loop in-process.
/// </summary>
/// <remarks>
/// Requests go to the URL in the <c>ANTHROPIC_BASE_URL</c> environment variable when it is set, for example a proxy,
/// and carry the API key there.
/// </remarks>
[Category("AI")]
[Description("AI agent using the Anthropic API with an API key.")]
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "The in-process turn adapts between the Anthropic SDK, its exception hierarchy, Microsoft.Extensions.AI and the agent types; that coupling is the adapter's purpose.")]
public partial class AnthropicAgent : LlmAgentBase
{
    private const int SummaryMaxOutputTokens = 4096;
    private const int ExtraToolIterations = 10;

    // One connection pool for every agent and turn: the API key travels per request, not per client. The SDK applies
    // its own request timeout, which the default HttpClient timeout of 100 seconds would otherwise cut short.
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly ILogger<AnthropicAgent> _logger;

    // Only read and written while the base class holds the turn gate.
    private AnthropicHistory _history = new();
    private IChatClient? _chatClient;
    private string? _chatClientApiKey;
    private int _chatClientMaxTokens;
    private bool _isMeasuringCompaction;
    private long _compactionFloor;

    public AnthropicAgent(AgentServices services, ILogger<AnthropicAgent> logger)
        : base(services, logger)
    {
        _logger = logger;
        MaxTokens = 16000;
        HistoryTokenBudget = 100_000;
    }

    /// <summary>The Anthropic API key.</summary>
    [Configuration(IsSecret = true)]
    public partial string? ApiKey { get; set; }

    /// <summary>Output token limit per model request, including thinking tokens.</summary>
    [Configuration]
    public partial int MaxTokens { get; set; }

    /// <summary>
    /// Input tokens of a chat request above which older chat messages are summarized; zero or less never summarizes.
    /// </summary>
    [Configuration]
    public partial int HistoryTokenBudget { get; set; }

    /// <summary>The HTTP client that sends the API requests; it is not disposed by the agent.</summary>
    protected virtual HttpClient HttpClient => SharedHttpClient;

    /// <summary>
    /// Creates the chat client for an API key and output token limit. The agent reuses it while both stay the same
    /// and never disposes it.
    /// </summary>
    /// <param name="apiKey">The API key, never empty.</param>
    /// <param name="maxOutputTokens">The default output token limit of a request.</param>
    protected virtual IChatClient CreateChatClient(string apiKey, int maxOutputTokens) =>
        // An explicit null auth token keeps the SDK from picking up a token from the environment.
        new AnthropicClient { ApiKey = apiKey, AuthToken = null, HttpClient = HttpClient }
            .AsIChatClient(defaultModelId: null, defaultMaxOutputTokens: maxOutputTokens);

    protected override async Task OnStartingAsync(CancellationToken cancellationToken)
    {
        ResetCompactionState();
        var json = await ReadSessionAsync(cancellationToken);
        try
        {
            _history = json is null ? new AnthropicHistory() : AnthropicHistory.Deserialize(json);
        }
        catch (JsonException exception)
        {
            // A damaged session file only loses the chat context; it must not keep the agent from starting.
            _logger.LogWarning(exception, "The session file of agent {Agent} could not be read; a new session starts.", Title);
            _history = new AnthropicHistory();
        }
    }

    protected override async Task ResetSessionAsync(CancellationToken cancellationToken)
    {
        _history = new AnthropicHistory();
        ResetCompactionState();
        await WriteSessionAsync(null, cancellationToken);
    }

    protected override async Task<AgentTurnResult> ExecuteTurnAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        var (apiKey, model, maxTokens) = GetValidatedConfiguration(request);
        var isChat = request.Kind == AgentTurnKind.Chat;
        var messages = isChat
            ? _history.BuildRequestMessages(request.SystemPrompt, request.UserMessage)
            : [new ChatMessage(ChatRole.System, request.SystemPrompt), new ChatMessage(ChatRole.User, request.UserMessage)];
        var options = AnthropicChatOptions.Create(model, maxTokens, request.Effort, request.Tools.Tools);

        var innerClient = GetChatClient(apiKey, maxTokens);

        // The tool set's budget governs: over it, calls get an error result. The client's own limits only stop a
        // model that ignores those errors, because its last iteration drops the tools, and a request whose tools
        // change mid-turn invalidates the thinking blocks the API expects back. Not disposed: that would dispose
        // the reused inner client.
        var iterationLimit = (int)Math.Min((long)Math.Max(MaxToolCallsPerTurn, 0) + ExtraToolIterations, int.MaxValue);
        var client = new FunctionInvokingChatClient(innerClient)
        {
            MaximumIterationsPerRequest = iterationLimit,
            MaximumConsecutiveErrorsPerRequest = iterationLimit
        };

        var updates = new List<ChatResponseUpdate>();
        long? firstRequestInputTokens = null;
        long lastRequestInputTokens = 0;
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
                TrackRequestInputTokens(update, ref firstRequestInputTokens, ref lastRequestInputTokens);

                if (!string.IsNullOrEmpty(update.Text))
                {
                    progress.Report(new AgentTextDelta(update.Text));
                }
            }
        }
        catch (Exception exception) when (Classify(exception, apiKey, cancellationToken) is { } failure)
        {
            throw failure;
        }

        var response = updates.ToChatResponse();
        var text = GetFinalText(response);
        if (string.IsNullOrWhiteSpace(text))
        {
            ThrowIfNotAnswered(response.FinishReason, maxTokens);
        }

        if (isChat)
        {
            _history.Append(new ChatMessage(ChatRole.User, request.UserMessage), response.Messages);
            await SaveHistoryAsync();
            await CompactHistoryIfNeededAsync(
                innerClient, apiKey, model, maxTokens, firstRequestInputTokens ?? 0, lastRequestInputTokens, cancellationToken);
        }

        return new AgentTurnResult(text, ToAgentUsage(response.Usage), RateLimit: null);
    }

    private (string ApiKey, string Model, int MaxTokens) GetValidatedConfiguration(AgentTurnRequest request)
    {
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AgentTurnException(AgentFailureKind.Configuration, "Set the API key of the agent.");
        }

        var model = request.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new AgentTurnException(AgentFailureKind.Configuration, "Set the model of the agent.");
        }

        var maxTokens = MaxTokens;
        if (maxTokens <= 0)
        {
            throw new AgentTurnException(AgentFailureKind.Configuration, "Set the output token limit of the agent to a positive number.");
        }

        return (apiKey, model, maxTokens);
    }

    private static void TrackRequestInputTokens(ChatResponseUpdate update, ref long? firstRequestInputTokens, ref long lastRequestInputTokens)
    {
        foreach (var content in update.Contents)
        {
            // One usage per model request: the first shows the context this turn started from, the last the
            // context the next turn starts from. Their sum, as in the response usage, says neither.
            if (content is UsageContent usageContent)
            {
                lastRequestInputTokens = usageContent.Details.InputTokenCount ?? 0;
                firstRequestInputTokens ??= lastRequestInputTokens;
            }
        }
    }

    private static void ThrowIfNotAnswered(ChatFinishReason? finishReason, int maxTokens)
    {
        if (finishReason == ChatFinishReason.ContentFilter)
        {
            throw new AgentTurnException(AgentFailureKind.Transient, "The model declined to answer.");
        }

        if (finishReason == ChatFinishReason.Length)
        {
            throw new AgentTurnException(
                AgentFailureKind.Transient, $"The model reached the output token limit of {maxTokens} tokens before answering.");
        }
    }

    private IChatClient GetChatClient(string apiKey, int maxTokens)
    {
        if (_chatClient is null || _chatClientApiKey != apiKey || _chatClientMaxTokens != maxTokens)
        {
            _chatClient = CreateChatClient(apiKey, maxTokens);
            _chatClientApiKey = apiKey;
            _chatClientMaxTokens = maxTokens;
        }

        return _chatClient;
    }

    /// <summary>
    /// The answer of a turn: the assistant text after the last tool results. Text written before a tool call is
    /// streamed but is not the answer, which must start with the quiet marker of a background run, for example.
    /// </summary>
    private static string GetFinalText(ChatResponse response)
    {
        var messages = response.Messages;
        var start = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.Tool)
            {
                start = index + 1;
                break;
            }
        }

        var text = new StringBuilder();
        for (var index = start; index < messages.Count; index++)
        {
            if (messages[index].Role == ChatRole.Assistant)
            {
                text.Append(messages[index].Text);
            }
        }

        return text.ToString();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S6667", Justification = "The cancellation is an expected stop, and the failure is logged with its redacted message only because the exception text can echo the API key.")]
    private async Task CompactHistoryIfNeededAsync(
        IChatClient client, string apiKey, string model, int maxTokens, long firstRequestInputTokens, long lastRequestInputTokens,
        CancellationToken cancellationToken)
    {
        long budget = HistoryTokenBudget;
        if (budget <= 0)
        {
            return;
        }

        if (_isMeasuringCompaction)
        {
            // The first request after a compaction shows what it achieved. When it stayed over the budget, for example
            // because the system prompt and tools alone exceed it, summarizing again only pays off after real growth.
            _isMeasuringCompaction = false;
            _compactionFloor = firstRequestInputTokens > budget ? firstRequestInputTokens + budget / 2 : 0;
        }

        if (lastRequestInputTokens <= Math.Max(budget, _compactionFloor))
        {
            return;
        }

        AnthropicCompactionOutcome outcome;
        try
        {
            outcome = await _history.CompactAsync(
                client,
                // Low effort rather than no thinking, which some models reject.
                new ChatOptions
                {
                    ModelId = model,
                    MaxOutputTokens = Math.Min(maxTokens, SummaryMaxOutputTokens),
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }
                },
                // Aims at half the budget, so that a few turns fit before the next compaction.
                keepFraction: Math.Clamp(budget / 2.0 / lastRequestInputTokens, 0, 0.5),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The answer is saved and must reach the timeline; the history is summarized after a later turn.
            _logger.LogInformation("Summarizing the chat history of agent {Agent} was cancelled.", Title);
            return;
        }
        catch (Exception exception)
        {
            // The turn already has its answer; the history stays as it was.
            _logger.LogWarning(
                "The chat history of agent {Agent} could not be summarized: {Message}",
                Title, Redact(exception.Message, apiKey));
            outcome = AnthropicCompactionOutcome.SummaryIncomplete;
        }

        switch (outcome)
        {
            case AnthropicCompactionOutcome.Compacted:
                _isMeasuringCompaction = true;
                await SaveHistoryAsync();
                break;
            case AnthropicCompactionOutcome.SummaryIncomplete:
                // A failing summary would otherwise cost a model call on every turn; retried after real growth.
                _compactionFloor = lastRequestInputTokens + budget / 2;
                _logger.LogWarning("The chat history of agent {Agent} was not summarized; it is retried once the chat has grown.", Title);
                break;
        }
    }

    private void ResetCompactionState()
    {
        _isMeasuringCompaction = false;
        _compactionFloor = 0;
    }

    private async Task SaveHistoryAsync()
    {
        string json;
        try
        {
            json = _history.Serialize();
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The chat history of agent {Agent} could not be serialized.", Title);
            return;
        }

        // Not cancellable: the answer is already in the history, so the file must follow it.
        await WriteSessionAsync(json, CancellationToken.None);
    }

    private static AgentUsage? ToAgentUsage(UsageDetails? details)
    {
        if (details is null)
        {
            return null;
        }

        // The input count of Microsoft.Extensions.AI includes cache reads and writes; AgentUsage counts them apart.
        var cacheRead = details.CachedInputTokenCount ?? 0;
        var cacheCreation = details.AdditionalCounts?.TryGetValue("CacheCreationInputTokens", out var count) == true ? count : 0;
        var input = Math.Max((details.InputTokenCount ?? 0) - cacheRead - cacheCreation, 0);
        return new AgentUsage(input, details.OutputTokenCount ?? 0, cacheRead, cacheCreation);
    }

    private static AgentTurnException? Classify(Exception exception, string apiKey, CancellationToken cancellationToken)
    {
        return exception switch
        {
            AgentTurnException => null,
            OperationCanceledException when cancellationToken.IsCancellationRequested => null,
            OperationCanceledException => Failure(AgentFailureKind.Transient, "The Anthropic API did not answer in time.", apiKey, exception),
            AnthropicApiException api => ClassifyApiException(api, apiKey),
            AnthropicSseException or AnthropicInvalidDataException => Failure(
                AgentFailureKind.Transient, "The Anthropic API failed while answering: " + DescribeEmbeddedError(exception.Message), apiKey, exception),
            AnthropicException or HttpRequestException => Failure(
                AgentFailureKind.Transient, "The Anthropic API could not be reached: " + exception.Message, apiKey, exception),
            _ => null
        };
    }

    private static AgentTurnException ClassifyApiException(AnthropicApiException exception, string apiKey)
    {
        return exception switch
        {
            AnthropicUnauthorizedException => Failure(AgentFailureKind.Configuration, "The Anthropic API rejected the API key.", apiKey, exception),
            AnthropicForbiddenException => Failure(
                AgentFailureKind.Configuration, "The API key may not use this model or feature: " + Describe(exception), apiKey, exception),
            AnthropicNotFoundException => Failure(
                AgentFailureKind.Configuration, "The Anthropic API did not find the model: " + Describe(exception), apiKey, exception),
            AnthropicRateLimitException => Failure(
                AgentFailureKind.UsageLimit, "The Anthropic API rate limit is reached: " + Describe(exception), apiKey, exception),
            // Transient: background runs do not use the chat history, and a chat recovers with a new conversation.
            AnthropicBadRequestException when IsPromptTooLong(exception) => Failure(
                AgentFailureKind.Transient,
                "The request is too long for the model: " + Describe(exception) + ". In the chat, start a new conversation.",
                apiKey, exception),
            _ => Failure(AgentFailureKind.Transient, "The Anthropic API failed: " + Describe(exception), apiKey, exception)
        };
    }

    private static AgentTurnException Failure(AgentFailureKind kind, string message, string apiKey, Exception exception) =>
        new(kind, Redact(message, apiKey), innerException: exception);

    private static bool IsPromptTooLong(AnthropicApiException exception) =>
        TryGetErrorMessage(exception.ResponseBody) is { } message &&
        message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase);

    private static string Describe(AnthropicApiException exception)
    {
        var status = (int)exception.StatusCode;
        return TryGetErrorMessage(exception.ResponseBody) is { } message ? $"{message} ({status})" : $"status code {status}";
    }

    // A stream error carries the API's error object inside its message, for example an overloaded error mid-answer.
    private static string DescribeEmbeddedError(string message)
    {
        var start = message.IndexOf('{');
        var end = message.LastIndexOf('}');
        return start >= 0 && end > start && TryGetErrorMessage(message[start..(end + 1)]) is { } errorMessage
            ? errorMessage
            : message;
    }

    private static bool TryGetProperty(JsonElement element, string name, JsonValueKind kind, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind == kind;

    private static string? TryGetErrorMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetProperty(document.RootElement, "error", JsonValueKind.Object, out var error) &&
                TryGetProperty(error, "message", JsonValueKind.String, out var message))
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // Not an error object; the caller describes the failure without it.
        }

        return null;
    }

    // The SDK does not put the key into exception messages; this guards against a response that echoes it.
    private static string Redact(string message, string? apiKey) =>
        string.IsNullOrEmpty(apiKey) ? message : message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
}
