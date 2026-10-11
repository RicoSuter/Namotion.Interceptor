using System.ComponentModel;
using System.Net;
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
[Category("AI")]
[Description("AI agent using the Anthropic API with an API key.")]
[InterceptorSubject]
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

    /// <summary>Input tokens of a chat request above which the older half of the history is summarized.</summary>
    [Configuration]
    public partial int HistoryTokenBudget { get; set; }

    /// <summary>Creates the chat client of one turn; the caller disposes it.</summary>
    /// <param name="apiKey">The API key, never empty.</param>
    /// <param name="maxOutputTokens">The default output token limit of a request.</param>
    protected virtual IChatClient CreateChatClient(string apiKey, int maxOutputTokens) =>
        // An explicit null auth token keeps the SDK from picking up a token from the environment.
        new AnthropicClient { ApiKey = apiKey, AuthToken = null, HttpClient = SharedHttpClient }
            .AsIChatClient(defaultModelId: null, defaultMaxOutputTokens: maxOutputTokens);

    protected override async Task OnStartingAsync(CancellationToken cancellationToken)
    {
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
        await WriteSessionAsync(null, cancellationToken);
    }

    protected override async Task<AgentTurnResult> ExecuteTurnAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AgentTurnException(AgentFailureKind.Configuration, "Set the API key of the agent.");
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new AgentTurnException(AgentFailureKind.Configuration, "Set the model of the agent.");
        }

        var maxTokens = MaxTokens;
        if (maxTokens <= 0)
        {
            throw new AgentTurnException(AgentFailureKind.Configuration, "Set the output token limit of the agent to a positive number.");
        }

        var isChat = request.Kind == AgentTurnKind.Chat;
        var messages = isChat
            ? _history.BuildRequestMessages(request.SystemPrompt, request.UserMessage)
            : [new ChatMessage(ChatRole.System, request.SystemPrompt), new ChatMessage(ChatRole.User, request.UserMessage)];
        var options = AnthropicChatOptions.Create(request.Model, maxTokens, request.Effort, request.Tools.Tools);

        using var innerClient = CreateChatClient(apiKey, maxTokens);
        using var client = new FunctionInvokingChatClient(innerClient)
        {
            // The tool set's budget governs: over it, calls get an error result. The client's own limit only stops a
            // model that ignores those errors, because its last iteration drops the tools, and a request whose tools
            // change mid-turn invalidates the thinking blocks the API expects back.
            MaximumIterationsPerRequest = (int)Math.Min((long)Math.Max(MaxToolCallsPerTurn, 0) + ExtraToolIterations, int.MaxValue)
        };

        var updates = new List<ChatResponseUpdate>();
        long lastRequestInputTokens = 0;
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
                foreach (var content in update.Contents)
                {
                    // One usage per model request; the last one measures the context the next chat turn starts from.
                    if (content is UsageContent usageContent)
                    {
                        lastRequestInputTokens = usageContent.Details.InputTokenCount ?? 0;
                    }
                }

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
        if (string.IsNullOrWhiteSpace(response.Text))
        {
            if (response.FinishReason == ChatFinishReason.ContentFilter)
            {
                throw new AgentTurnException(AgentFailureKind.Transient, "The model declined to answer.");
            }

            if (response.FinishReason == ChatFinishReason.Length)
            {
                throw new AgentTurnException(
                    AgentFailureKind.Transient, $"The model reached the output token limit of {maxTokens} tokens before answering.");
            }
        }

        if (isChat)
        {
            _history.Append(new ChatMessage(ChatRole.User, request.UserMessage), response.Messages);
            await SaveHistoryAsync();
            if (lastRequestInputTokens > HistoryTokenBudget)
            {
                await CompactHistoryAsync(innerClient, request.Model, maxTokens, cancellationToken);
            }
        }

        return new AgentTurnResult(response.Text, ToAgentUsage(response.Usage), RateLimit: null);
    }

    private async Task CompactHistoryAsync(IChatClient client, string model, int maxTokens, CancellationToken cancellationToken)
    {
        var messageCount = _history.Messages.Count;
        try
        {
            await _history.CompactAsync(
                client,
                new ChatOptions { ModelId = model, MaxOutputTokens = Math.Min(maxTokens, SummaryMaxOutputTokens) },
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The turn already has its answer; the history stays as it was and is summarized after a later turn.
            _logger.LogWarning(
                "The chat history of agent {Agent} could not be summarized: {Message}",
                Title, Redact(exception.Message, ApiKey));
            return;
        }

        if (_history.Messages.Count != messageCount)
        {
            await SaveHistoryAsync();
        }
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
            OperationCanceledException => Failure(AgentFailureKind.Transient, "The Anthropic API did not answer in time."),
            AnthropicUnauthorizedException => Failure(AgentFailureKind.Configuration, "The Anthropic API rejected the API key."),
            AnthropicForbiddenException api => Failure(AgentFailureKind.Configuration, "The API key may not use this model or feature: " + Describe(api)),
            AnthropicNotFoundException api => Failure(AgentFailureKind.Configuration, "The Anthropic API did not find the model: " + Describe(api)),
            AnthropicRateLimitException api => Failure(AgentFailureKind.UsageLimit, "The Anthropic API rate limit is reached: " + Describe(api)),
            AnthropicApiException api => Failure(AgentFailureKind.Transient, "The Anthropic API failed: " + Describe(api)),
            AnthropicException or HttpRequestException => Failure(AgentFailureKind.Transient, "The Anthropic API could not be reached: " + exception.Message),
            _ => null
        };

        AgentTurnException Failure(AgentFailureKind kind, string message) =>
            new(kind, Redact(message, apiKey), innerException: exception);
    }

    private static string Describe(AnthropicApiException exception)
    {
        var status = (int)exception.StatusCode;
        try
        {
            using var document = JsonDocument.Parse(exception.ResponseBody);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return $"{message.GetString()} ({status})";
            }
        }
        catch (JsonException)
        {
        }

        return $"status code {status}";
    }

    // The SDK does not put the key into exception messages; this guards against a response that echoes it.
    private static string Redact(string message, string? apiKey) =>
        string.IsNullOrEmpty(apiKey) ? message : message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
}
