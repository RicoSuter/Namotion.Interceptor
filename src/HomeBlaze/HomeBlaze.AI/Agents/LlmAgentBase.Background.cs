using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.AI.Agents;

public abstract partial class LlmAgentBase
{
    private const int RecentResultCount = 5;

    /// <summary>Starts a background run now, also while automatic runs are paused.</summary>
    [Operation(Title = "Run Now", Icon = "PlayArrow", Position = 1)]
    public void RunNow()
    {
        lock (_stateLock)
        {
            _scheduler?.RequestManualRun();
        }
    }

    private bool IsWatchCandidate(PropertyReference property)
    {
        // Agents never trigger on agent state: two agents watching each other's busy and status
        // properties would otherwise keep triggering each other.
        if (Volatile.Read(ref _normalizedWatchPaths).Length == 0 || property.Subject is LlmAgentBase)
        {
            return false;
        }

        return property.TryGetRegisteredProperty()?.TryGetAttribute(KnownAttributes.State) is not null;
    }

    private ValueTask OnChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, ISubjectPathResolver? resolver)
    {
        var watchPaths = Volatile.Read(ref _normalizedWatchPaths);
        if (resolver is null || watchPaths.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        if (_configurationError is not null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Ignored {Count} changes of agent {Agent} because its trigger configuration is invalid.", changes.Length, Title);
            }

            return ValueTask.CompletedTask;
        }

        if (CanRequestRun())
        {
            RequestRunOnWatchedChange(changes.Span, resolver, watchPaths);
        }

        return ValueTask.CompletedTask;
    }

    private void RequestRunOnWatchedChange(ReadOnlySpan<SubjectPropertyChange> changes, ISubjectPathResolver resolver, string[] watchPaths)
    {
        foreach (var change in changes)
        {
            var subjectPath = resolver.GetPath(change.Property.Subject, PathStyle.Canonical);
            if (subjectPath is not null && AgentWatchPaths.Matches(subjectPath, change.Property.Name, watchPaths))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("The change of {Property} on {Subject} requests a background run of agent {Agent}.", change.Property.Name, subjectPath, Title);
                }

                lock (_stateLock)
                {
                    _scheduler?.RequestRun(AgentTriggers.Change);
                }

                return;
            }
        }
    }

    /// <summary>
    /// Whether a change request could start a run: the agent runs and no run is pending, since a request would
    /// coalesce into the pending run, which reads the watched state only when it starts.
    /// </summary>
    private bool CanRequestRun()
    {
        lock (_stateLock)
        {
            return _scheduler is { IsRunPending: false };
        }
    }

    // The scheduler callback: an exception escaping it ends the scheduler loop, so only cancellation of the
    // stopping token may leave this method. Every started run counts, also one that fails or is cancelled.
    private async Task RunBackgroundAsync(string reason, CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken);
        DateTimeOffset? started = null;
        AgentToolSet? tools = null;
        var isCounted = false;
        var isIdle = false;
        try
        {
            started = BeginTurn(BackgroundRunStatusMessage);
            var progress = CreateProgress();
            var allowedTools = AllowedTools;
            tools = CreateToolSet(allowedTools, progress);
            var message = AgentPromptBuilder.BuildBackgroundMessage(
                reason,
                Timeline.GetRecentBackgroundResults(RecentResultCount),
                Timeline.GetLastQuietRunTime(),
                await AgentWatchPaths.FetchStateAsync(Volatile.Read(ref _normalizedWatchPaths), _services.ToolSource, cancellationToken),
                started.Value);

            var result = await RunTurnAsync(AgentTurnKind.Background, message, allowedTools, tools, progress, cancellationToken);
            var isQuiet = AgentPromptBuilder.IsQuiet(result.FinalText);
            ClearStreamingText();
            await AppendAsync(new AgentTimelineEntry
            {
                Kind = AgentTimelineEntryKind.BackgroundRun,
                Timestamp = started.Value,
                Text = result.Text,
                FinalText = result.FinalText == result.Text ? null : result.FinalText,
                IsQuiet = isQuiet,
                Trigger = reason,
                ToolCalls = tools.Calls,
                Duration = _services.TimeProvider.GetUtcNow() - started,
                Usage = result.Usage
            });

            if (!isQuiet)
            {
                LastAnalysis = result.FinalText;
            }

            isCounted = true;
            await CountBackgroundRunAsync(started.Value);
            lock (_stateLock)
            {
                _scheduler?.ReportSuccess();
            }

            isIdle = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (started is { } cancelledStart && !isCounted)
            {
                await AppendAsync(CreateBackgroundErrorEntry(CancelledText, reason, cancelledStart, tools));
                await CountBackgroundRunAsync(cancelledStart);
            }

            throw;
        }
        catch (Exception exception)
        {
            try
            {
                var turnException = ToTurnException(exception);
                if (started is { } failedStart && !isCounted)
                {
                    await CountBackgroundRunAsync(failedStart);
                    await HandleFailureAsync(turnException, CreateBackgroundErrorEntry(turnException.Message, reason, failedStart, tools));
                }
                else
                {
                    // Not started, or its entry with the tool calls is already recorded.
                    await HandleFailureAsync(turnException, CreateErrorEntry(turnException.Message, tools: null));
                }
            }
            catch (Exception handlerException)
            {
                _logger.LogError(handlerException, "The failure of agent {Agent} could not be recorded.", Title);
            }
        }
        finally
        {
            EndTurn(isIdle);
            _turnGate.Release();
        }
    }

    /// <summary>Counts a background run and persists the count; the caller holds the turn gate.</summary>
    private Task CountBackgroundRunAsync(DateTimeOffset started)
    {
        LastRunTime = started;
        RunCount++;
        return WriteRunStatisticsAsync();
    }

    /// <summary>The error entry of a background run, with the calls of its tool set when it has one.</summary>
    private AgentTimelineEntry CreateBackgroundErrorEntry(string text, string trigger, DateTimeOffset started, AgentToolSet? tools) => new()
    {
        Kind = AgentTimelineEntryKind.Error,
        Timestamp = started,
        Text = text,
        Trigger = trigger,
        ToolCalls = tools?.Calls ?? [],
        Duration = _services.TimeProvider.GetUtcNow() - started
    };
}
