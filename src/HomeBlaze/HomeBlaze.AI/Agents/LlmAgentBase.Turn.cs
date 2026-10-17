using HomeBlaze.Abstractions;
using HomeBlaze.Storage;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

public abstract partial class LlmAgentBase
{
    /// <summary>Runs a turn and closes its tool set when it ends, so a tool call that arrives later runs nothing.</summary>
    private async Task<AgentTurnResult> RunTurnAsync(
        AgentTurnKind kind,
        string message,
        string[] allowedTools,
        AgentToolSet tools,
        IProgress<AgentTurnUpdate> progress,
        CancellationToken cancellationToken)
    {
        var request = CreateTurnRequest(kind, message, allowedTools, tools);
        var turnTimeout = GetEffectiveTurnTimeout();
        using var timeout = turnTimeout is { } limit ? new CancellationTokenSource(limit, _services.TimeProvider) : null;
        using var turnCancellation = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var result = await ExecuteTurnAsync(request, progress, turnCancellation?.Token ?? cancellationToken);
            if (result.RateLimit is { } rateLimit)
            {
                FiveHourUsage = rateLimit.FiveHourUtilization;
                SevenDayUsage = rateLimit.SevenDayUtilization;
            }

            return result;
        }
        catch (OperationCanceledException) when (timeout is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            throw new AgentTurnException(AgentFailureKind.Transient, $"The turn did not finish within {turnTimeout}.");
        }
        finally
        {
            tools.Close();
        }
    }

    private AgentTurnRequest CreateTurnRequest(AgentTurnKind kind, string message, string[] allowedTools, AgentToolSet tools)
    {
        var isBackground = kind == AgentTurnKind.Background;
        var backendTools = SupportedBackendTools.Where(tool => allowedTools.Contains(tool.Name)).ToList();
        return new AgentTurnRequest(
            kind,
            AgentPromptBuilder.BuildSystemPrompt(
                kind, Instructions, Memory.GetSnapshot(), backendTools.Exists(tool => tool.LoadsWebContent), AgentToolSet.CanEditPageLayout(allowedTools)),
            message,
            isBackground ? BackgroundModel ?? Model : Model,
            isBackground ? BackgroundEffort ?? Effort : Effort,
            tools)
        {
            BackendTools = backendTools.ConvertAll(tool => tool.Name)
        };
    }

    private AgentToolSet CreateToolSet(string[] allowedTools, IProgress<AgentTurnUpdate> progress)
    {
        var canEditPageLayout = AgentToolSet.CanEditPageLayout(allowedTools);
        var available = _services.ToolSource.CreateTools(AgentToolSet.IsReadOnly(allowedTools))
            .Select(tool => WithPageLayoutPermission(tool, grantsPageLayout: canEditPageLayout && AgentToolSet.WritesPages(tool.Name)))
            .ToList();
        return AgentToolSet.Create(available, allowedTools, Memory.CreateUpdateTool(), MaxToolCallsPerTurn, progress);
    }

    // A backend may call tools on a flow that does not descend from RunTurnAsync, such as an HTTP request of
    // the MCP route, so each call sets the permission to edit page layout, granted or not, on its own flow only.
    private static McpToolInfo WithPageLayoutPermission(McpToolInfo tool, bool grantsPageLayout) => new(tool)
    {
        Handler = async (input, cancellationToken) =>
        {
            using (MarkdownPageLayoutPermission.Enter(grantsPageLayout))
            {
                return await tool.Handler(input, cancellationToken);
            }
        }
    };

    private SynchronousProgress<AgentTurnUpdate> CreateProgress() => new(update =>
    {
        if (update is AgentTextDelta delta)
        {
            lock (_streamingLock)
            {
                _streamingText.Append(delta.Text);
            }
        }

        RaiseStateChanged();
    });

    // Also cleared before the final entry of a turn is appended, so its text never shows twice: as the entry and
    // as streamed text.
    private void ClearStreamingText()
    {
        lock (_streamingLock)
        {
            _streamingText.Clear();
        }
    }

    private DateTimeOffset BeginTurn(string statusMessage)
    {
        ClearStreamingText();
        IsBusy = true;
        SetStatusIfStarted(ServiceStatus.Running, statusMessage);
        var started = _services.TimeProvider.GetUtcNow();
        LastActivityTime = started;
        return started;
    }

    /// <summary>Ends a turn; <paramref name="isIdle"/> reports the idle status once the agent is no longer busy.</summary>
    private void EndTurn(bool isIdle)
    {
        ClearStreamingText();

        // Cleared before the idle status, so a configuration change that skipped SetIdle while busy is picked up.
        IsBusy = false;
        if (isIdle)
        {
            SetIdle();
        }
    }

    private AgentTurnException ToTurnException(Exception exception)
    {
        if (exception is AgentTurnException turnException)
        {
            return turnException;
        }

        _logger.LogError(exception, "A turn of agent {Agent} failed unexpectedly.", Title);
        return new AgentTurnException(AgentFailureKind.Transient, exception.Message, innerException: exception);
    }

    private async Task HandleFailureAsync(AgentTurnException exception, AgentTimelineEntry errorEntry)
    {
        _logger.LogWarning("The turn of agent {Agent} failed: {Message}", Title, exception.Message);
        lock (_stateLock)
        {
            _scheduler?.ReportFailure(exception);
        }

        // Recorded before the status changes, so a reader that sees the failure status also sees the entry.
        await AppendAsync(errorEntry);
        SetStatusIfStarted(
            exception.Kind is AgentFailureKind.Configuration or AgentFailureKind.UsageLimit
                ? ServiceStatus.Unavailable
                : ServiceStatus.Error,
            exception.Message);
    }

    /// <summary>
    /// The error entry of a chat turn or a failure outside a turn, with the calls of the tool set of the turn when it
    /// has one, so a change the turn made before it failed stays recorded.
    /// </summary>
    private AgentTimelineEntry CreateErrorEntry(string text, AgentToolSet? tools) => new()
    {
        Kind = AgentTimelineEntryKind.Error,
        Timestamp = _services.TimeProvider.GetUtcNow(),
        Text = text,
        ToolCalls = tools?.Calls ?? []
    };
}
