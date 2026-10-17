using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
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
                await FetchWatchedStateAsync(cancellationToken),
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

    /// <summary>
    /// Persists the run count and the last activity, also while the host stops; the caller holds the turn gate.
    /// </summary>
    private Task WriteRunStatisticsAsync() => TryWriteDataFileAsync(
        AgentStorage.StatsSuffix,
        JsonSerializer.Serialize(new AgentStats(RunCount, LastActivityTime), AgentJsonContext.Default.AgentStats),
        "after a restart the run count and the last activity may be older",
        CancellationToken.None);

    /// <summary>
    /// Restores the run count and the last activity from the stats file, none when it is missing, and the last run
    /// from the timeline; the caller holds the turn gate.
    /// </summary>
    private async Task RestoreRunStatisticsAsync(CancellationToken cancellationToken)
    {
        var statistics = await TryReadJsonDataFileAsync(
            AgentStorage.StatsSuffix,
            content => JsonSerializer.Deserialize(content, AgentJsonContext.Default.AgentStats),
            "the run count starts at zero and the last activity is unknown",
            cancellationToken);
        LastRunTime = Timeline.GetLastBackgroundRunTime();
        RunCount = statistics?.BackgroundRunCount ?? 0;
        LastActivityTime = statistics?.LastActivityTime;
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

    private async Task<IReadOnlyList<AgentWatchedState>> FetchWatchedStateAsync(CancellationToken cancellationToken)
    {
        var watchPaths = Volatile.Read(ref _normalizedWatchPaths);
        if (watchPaths.Length == 0)
        {
            return [];
        }

        var tools = _services.ToolSource.CreateTools(isReadOnly: true);
        var browse = tools.FirstOrDefault(tool => tool.Name == "browse");
        var getProperty = tools.FirstOrDefault(tool => tool.Name == "get_property");
        var states = new List<AgentWatchedState>(watchPaths.Length);
        foreach (var path in watchPaths)
        {
            try
            {
                states.Add(await FetchPathStateAsync(path, browse, getProperty, cancellationToken));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                states.Add(new AgentWatchedState(path, AgentWatchedStateKind.Error, ToJsonString("Error: " + exception.Message)));
            }
        }

        return states;
    }

    /// <summary>
    /// Browses the subject at a watch path, or reads the property there when no subject is, since browse only
    /// resolves subjects.
    /// </summary>
    private static async Task<AgentWatchedState> FetchPathStateAsync(
        string path, McpToolInfo? browse, McpToolInfo? getProperty, CancellationToken cancellationToken)
    {
        string? error = null;
        if (browse is not null)
        {
            var input = JsonSerializer.SerializeToElement(new AgentBrowseInput(path, Depth: 2, IncludeProperties: true, Format: "text"), AgentJsonContext.Default.AgentBrowseInput);
            var result = await browse.Handler(input, cancellationToken);
            if (result is string text)
            {
                return new AgentWatchedState(path, AgentWatchedStateKind.Subject, ToJsonString(text));
            }

            // Tool results are open-ended objects, so only they are serialized without generated metadata.
            error = TryGetError(JsonSerializer.SerializeToElement(result));
        }

        if (getProperty is not null)
        {
            var input = JsonSerializer.SerializeToElement(new AgentPathInput(path), AgentJsonContext.Default.AgentPathInput);
            var result = JsonSerializer.SerializeToElement(await getProperty.Handler(input, cancellationToken));
            if (TryGetError(result) is null && result.ValueKind == JsonValueKind.Object)
            {
                return new AgentWatchedState(path, AgentWatchedStateKind.Property, result);
            }
        }

        return new AgentWatchedState(path, AgentWatchedStateKind.Error, ToJsonString(error ?? $"No subject or property found at {path}."));
    }

    private static JsonElement ToJsonString(string text) => JsonSerializer.SerializeToElement(text, AgentJsonContext.Default.String);

    private static string? TryGetError(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var error) ? error.ToString() : null;
}

internal sealed record AgentStats(int BackgroundRunCount, DateTimeOffset? LastActivityTime);

/// <summary>The input of the browse tool for the state of a watch path.</summary>
internal sealed record AgentBrowseInput(string Path, int Depth, bool IncludeProperties, string Format);

/// <summary>The input of the get_property tool for the state of a watch path.</summary>
internal sealed record AgentPathInput(string Path);
