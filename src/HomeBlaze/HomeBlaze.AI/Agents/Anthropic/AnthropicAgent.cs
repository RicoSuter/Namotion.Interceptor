using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
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
    private bool _isMeasuringCompaction;
    private long _compactionFloor;
    private PendingCompaction? _pendingCompaction;
    private AgentUsage? _unreportedUsage;

    public AnthropicAgent(AgentServices services, ILogger<AnthropicAgent> logger)
        : base(services, logger, AgentBackendTool.WebTools)
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
    /// Creates the chat client for an API key. The agent reuses it while the key stays the same and never disposes it.
    /// Every request sets its model and output token limit in its options.
    /// </summary>
    /// <param name="apiKey">The API key, never empty.</param>
    protected virtual IChatClient CreateChatClient(string apiKey) =>
        // Options rather than an initializer: the parameterless constructor first resolves credentials, and a local
        // Anthropic profile would set the base URL that then receives the key. AuthToken is null, as the options would
        // otherwise read ANTHROPIC_AUTH_TOKEN and send it as Authorization. ANTHROPIC_BASE_URL and
        // ANTHROPIC_CUSTOM_HEADERS of the process environment still apply; the operator sets them. Disposing the
        // client would dispose the shared HttpClient.
        new AnthropicClient(new ClientOptions { ApiKey = apiKey, AuthToken = null, HttpClient = HttpClient }).AsIChatClient();

    protected override async Task OnStartingAsync(CancellationToken cancellationToken)
    {
        ResetCompactionState();
        _history = await ReadSessionAsync(AnthropicHistory.Deserialize, cancellationToken) ?? new AnthropicHistory();
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
        // Set again only by a chat turn that succeeds, so a later turn never compacts with stale token counts.
        _pendingCompaction = null;
        var (apiKey, model, maxTokens) = GetValidatedConfiguration(request);
        var isChat = request.Kind == AgentTurnKind.Chat;
        var messages = isChat
            ? _history.BuildRequestMessages(request.SystemPrompt, request.UserMessage)
            : [new ChatMessage(ChatRole.System, request.SystemPrompt), new ChatMessage(ChatRole.User, request.UserMessage)];
        var options = AnthropicChatOptions.Create(model, maxTokens, request.Effort, request.Tools.Tools);

        var updates = new List<ChatResponseUpdate>();
        var answer = new AgentAnswerText();
        long? firstRequestInputTokens = null;
        long lastRequestInputTokens = 0;
        try
        {
            var innerClient = GetChatClient(apiKey);

            // The tool set's budget governs: over it, calls get an error result. The client's own limits only stop a
            // model that ignores those errors, because its last iteration drops the tools, and a request whose tools
            // change mid-turn invalidates the thinking blocks the API expects back. Neither client is disposed: that
            // would dispose the reused inner client.
            var iterationLimit = GetModelRoundLimit();
            var client = new FunctionInvokingChatClient(
                request.BackendTools.Count > 0
                    ? new AnthropicServerToolClient(innerClient, request.Tools, request.BackendTools, MaxToolCallsPerTurn)
                    : innerClient)
            {
                MaximumIterationsPerRequest = iterationLimit,
                MaximumConsecutiveErrorsPerRequest = iterationLimit
            };

            await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
                TrackRequestInputTokens(update, ref firstRequestInputTokens, ref lastRequestInputTokens);
                AppendAnswerText(answer, update, progress);
            }
        }
        catch (Exception exception) when (Classify(exception, apiKey, cancellationToken) is { } classification)
        {
            if (classification.IsUnexpected)
            {
                LogUnexpectedFailure(exception, apiKey);
            }

            throw classification.Failure;
        }

        var response = updates.ToChatResponse();

        // Text written before a tool call is streamed but is not the answer, which must start with the quiet marker of
        // a background run, for example.
        var finalText = answer.FinalSegment;
        if (string.IsNullOrWhiteSpace(finalText))
        {
            ThrowIfNotAnswered(response.FinishReason, maxTokens);
        }

        if (isChat)
        {
            _history.Append(new ChatMessage(ChatRole.User, request.UserMessage), response.Messages);
            await SaveHistoryAsync();
            _pendingCompaction = new PendingCompaction(model, maxTokens, firstRequestInputTokens ?? 0, lastRequestInputTokens);
        }

        // A summary runs after its turn is recorded, so its usage is reported with the next turn.
        var usage = Add(ToAgentUsage(response.Usage), _unreportedUsage);
        _unreportedUsage = null;
        return new AgentTurnResult(answer.Text ?? string.Empty, usage, RateLimit: null) { FinalText = finalText };
    }

    protected override async Task OnChatTurnCompletedAsync(CancellationToken cancellationToken)
    {
        // The cached client and key are still the ones of the turn that set the pending compaction.
        if (_pendingCompaction is { } pending && _chatClient is { } client && _chatClientApiKey is { } apiKey)
        {
            _pendingCompaction = null;
            await CompactHistoryIfNeededAsync(client, apiKey, pending, cancellationToken);
        }
    }

    // Text after a tool call or tool result, of a graph tool or a server tool, starts a new segment of the answer.
    private static void AppendAnswerText(AgentAnswerText answer, ChatResponseUpdate update, IProgress<AgentTurnUpdate> progress)
    {
        foreach (var content in update.Contents)
        {
            if (content is ToolCallContent or ToolResultContent)
            {
                answer.StartSegment();
            }
            else if (content is TextContent text)
            {
                answer.Append(text.Text, progress);
            }
        }
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

    private IChatClient GetChatClient(string apiKey)
    {
        if (_chatClient is null || _chatClientApiKey != apiKey)
        {
            _chatClient = CreateChatClient(apiKey);
            _chatClientApiKey = apiKey;
        }

        return _chatClient;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S6667", Justification = "The cancellation is an expected stop, and the failure is logged with its redacted message only because the exception text can echo the API key.")]
    private async Task CompactHistoryIfNeededAsync(
        IChatClient client, string apiKey, PendingCompaction pending, CancellationToken cancellationToken)
    {
        var (model, maxTokens, firstRequestInputTokens, lastRequestInputTokens) = pending;
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
            var compaction = await _history.CompactAsync(
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
            outcome = compaction.Outcome;
            _unreportedUsage = Add(_unreportedUsage, ToAgentUsage(compaction.Usage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The answer is already in the timeline; the history is summarized after a later turn.
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
        _pendingCompaction = null;
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

    private static AgentUsage? Add(AgentUsage? first, AgentUsage? second)
    {
        if (first is null || second is null)
        {
            return first ?? second;
        }

        return new AgentUsage(
            first.InputTokens + second.InputTokens,
            first.OutputTokens + second.OutputTokens,
            first.CacheReadInputTokens + second.CacheReadInputTokens,
            first.CacheCreationInputTokens + second.CacheCreationInputTokens);
    }

    private static (AgentTurnException Failure, bool IsUnexpected)? Classify(Exception exception, string apiKey, CancellationToken cancellationToken)
    {
        return exception switch
        {
            AgentTurnException => null,
            OperationCanceledException when cancellationToken.IsCancellationRequested => null,
            OperationCanceledException => (Failure(AgentFailureKind.Transient, "The Anthropic API did not answer in time.", apiKey, exception), false),
            AnthropicApiException api => (ClassifyApiException(api, apiKey), false),
            AnthropicSseException or AnthropicInvalidDataException => (Failure(
                AgentFailureKind.Transient, "The Anthropic API failed while answering: " + DescribeEmbeddedError(exception.Message), apiKey, exception), false),
            AnthropicException or HttpRequestException => (Failure(
                AgentFailureKind.Transient, "The Anthropic API could not be reached: " + exception.Message, apiKey, exception), false),
            // Redacted like the others: the base class would show and log the message as it is.
            _ => (Failure(AgentFailureKind.Transient, "The Anthropic client failed: " + exception.Message, apiKey, exception), true)
        };
    }

    private void LogUnexpectedFailure(Exception exception, string apiKey)
    {
        // Without the exception object, whose messages a log sink would write as they are; stack traces hold no key.
        _logger.LogError(
            "The Anthropic client of agent {Agent} failed unexpectedly with {ExceptionType}: {Message}{InnerExceptions}{NewLine}{StackTrace}",
            Title, exception.GetType().FullName, Redact(exception.Message, apiKey), DescribeInnerExceptions(exception, apiKey),
            Environment.NewLine, exception.StackTrace);
    }

    private static string DescribeInnerExceptions(Exception exception, string apiKey)
    {
        const int maximumCount = 8;
        var builder = new StringBuilder();
        var pending = new Queue<Exception>();
        EnqueueInnerExceptions(exception, pending);
        for (var count = 0; count < maximumCount && pending.TryDequeue(out var inner); count++)
        {
            builder.Append(Environment.NewLine).Append(" ---> ").Append(inner.GetType().FullName)
                .Append(": ").Append(Redact(inner.Message, apiKey));
            EnqueueInnerExceptions(inner, pending);
        }

        if (pending.Count > 0)
        {
            builder.Append(Environment.NewLine).Append(" ---> ...");
        }

        return builder.ToString();
    }

    // An aggregate's InnerException is only its first one.
    private static void EnqueueInnerExceptions(Exception exception, Queue<Exception> pending)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                pending.Enqueue(inner);
            }
        }
        else if (exception.InnerException is { } inner)
        {
            pending.Enqueue(inner);
        }
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

    private readonly record struct PendingCompaction(string Model, int MaxTokens, long FirstRequestInputTokens, long LastRequestInputTokens);

    // The SDK does not put the key into exception messages; this guards against a response that echoes it.
    private static string Redact(string message, string apiKey) =>
        apiKey.Length == 0 ? message : message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
}
