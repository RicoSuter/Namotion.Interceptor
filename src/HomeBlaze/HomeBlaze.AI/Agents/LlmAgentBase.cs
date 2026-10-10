using System.Text;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Base of the built-in agents: configuration, background triggers, chat, memory, timeline and tool scoping.
/// A subclass only executes a turn on its backend and resets its chat session.
/// </summary>
[InterceptorSubject]
public abstract partial class LlmAgentBase : BackgroundService, IConfigurable, IMonitoredService, ITitleProvider, IIconProvider
{
    private const int RecentResultCount = 5;
    private static readonly TimeSpan ChangeBufferTime = TimeSpan.FromMilliseconds(100);

    private readonly AgentServices _services;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly Lock _streamingLock = new();
    private readonly StringBuilder _streamingText = new();
    private AgentTriggerScheduler? _scheduler;
    private string[] _normalizedWatchPaths = [];
    private volatile string? _configurationError;
    private string? _storageDirectory;
    private string? _storageName;

    protected LlmAgentBase(AgentServices services, ILogger logger)
    {
        _services = services;
        _logger = logger;
        Timeline = new AgentTimeline(filePath: null);
        Memory = new AgentMemory(filePath: null, services.TimeProvider);
        WatchPaths = [];
        AllowedTools = [.. AgentToolSet.DefaultAllowedTools];
        MinRunInterval = TimeSpan.FromMinutes(1);
        TurnTimeout = TimeSpan.FromMinutes(5);
        MaxToolCallsPerTurn = 25;
        Status = ServiceStatus.Stopped;
    }

    [Configuration]
    public partial string? Name { get; set; }

    [Configuration]
    public partial string? Instructions { get; set; }

    [Configuration]
    public partial string[] WatchPaths { get; set; }

    [Configuration]
    public partial TimeSpan? PollInterval { get; set; }

    [Configuration]
    public partial TimeSpan MinRunInterval { get; set; }

    [Configuration]
    public partial string[] AllowedTools { get; set; }

    /// <summary>The time a turn may take; zero or less means no limit.</summary>
    [Configuration]
    public partial TimeSpan TurnTimeout { get; set; }

    [Configuration]
    public partial int MaxToolCallsPerTurn { get; set; }

    [Configuration]
    public partial string? Model { get; set; }

    [Configuration]
    public partial AgentEffort? Effort { get; set; }

    [Configuration]
    public partial string? BackgroundModel { get; set; }

    [Configuration]
    public partial AgentEffort? BackgroundEffort { get; set; }

    [State(IsDiscrete = true, Position = 1)]
    public partial ServiceStatus Status { get; internal set; }

    [State(Position = 2)]
    public partial string? StatusMessage { get; internal set; }

    [State(IsDiscrete = true, Position = 3)]
    public partial bool IsBusy { get; internal set; }

    [State("Last Analysis", Position = 4)]
    public partial string? LastAnalysis { get; internal set; }

    [State("Last Run", Position = 5)]
    public partial DateTimeOffset? LastRunTime { get; internal set; }

    [State("Run Count", Position = 6)]
    public partial int RunCount { get; internal set; }

    [State("Usage (5 hours)", Position = 7)]
    public partial double? FiveHourUsage { get; internal set; }

    [State("Usage (7 days)", Position = 8)]
    public partial double? SevenDayUsage { get; internal set; }

    [Derived]
    public string? Title => Name ?? GetType().Name;

    public string? IconName => "SmartToy";

    /// <summary>The timeline of background runs and chat.</summary>
    public AgentTimeline Timeline { get; private set; }

    /// <summary>The agent's memory document.</summary>
    public AgentMemory Memory { get; private set; }

    /// <summary>The text streamed so far by the running turn, empty when idle.</summary>
    public string StreamingText
    {
        get
        {
            lock (_streamingLock)
            {
                return _streamingText.ToString();
            }
        }
    }

    /// <summary>
    /// Raised when the timeline or the streaming state of the running turn changes, on the changing thread.
    /// Exceptions thrown by handlers are logged and swallowed.
    /// </summary>
    public event Action? TimelineChanged;

    /// <summary>The working directory for external processes of this agent.</summary>
    protected string WorkingDirectory { get; private set; } = Path.Combine(Path.GetTempPath(), "homeblaze-agent-" + Guid.NewGuid().ToString("N"));

    /// <summary>The services shared by agents.</summary>
    protected AgentServices Services => _services;

    /// <summary>Starts a background run now, also while automatic runs are paused.</summary>
    [Operation(Title = "Run Now", Icon = "PlayArrow", Position = 1)]
    public void RunNow() => _scheduler?.RequestManualRun();

    /// <summary>Discards the chat session; memory and timeline stay.</summary>
    [Operation(Title = "New Conversation", Icon = "RestartAlt", Position = 2, RequiresConfirmation = true)]
    public async Task NewConversationAsync(CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken);
        try
        {
            await ResetSessionAsync(cancellationToken);
            await AppendAsync(new AgentTimelineEntry
            {
                Kind = AgentTimelineEntryKind.Divider,
                Timestamp = _services.TimeProvider.GetUtcNow(),
                Text = "New conversation"
            });
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Sends a chat message and waits for the reply, which is appended to the timeline. Failures are recorded
    /// in the timeline instead of thrown; only cancellation through <paramref name="cancellationToken"/> throws.
    /// </summary>
    public async Task SendMessageAsync(string text, CancellationToken cancellationToken)
    {
        await AppendAsync(new AgentTimelineEntry
        {
            Kind = AgentTimelineEntryKind.User,
            Timestamp = _services.TimeProvider.GetUtcNow(),
            Text = text
        });

        if (!_turnGate.Wait(0))
        {
            StatusMessage = "Waiting for the background run";
            await _turnGate.WaitAsync(cancellationToken);
        }

        try
        {
            var started = BeginTurn("Answering");
            var progress = CreateProgress();
            var tools = CreateToolSet(progress);
            var message = AgentPromptBuilder.BuildChatMessage(text, Timeline.GetFindingsSinceLastReply());
            var result = await RunTurnAsync(AgentTurnKind.Chat, message, tools, progress, cancellationToken);
            await AppendAsync(new AgentTimelineEntry
            {
                Kind = AgentTimelineEntryKind.Assistant,
                Timestamp = _services.TimeProvider.GetUtcNow(),
                Text = result.Text,
                ToolCalls = tools.Calls,
                Duration = _services.TimeProvider.GetUtcNow() - started,
                Usage = result.Usage
            });

            // A working chat turn proves the backend works again, so it lifts pauses like a background run.
            _scheduler?.ReportSuccess();
            SetIdle();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await HandleFailureAsync(ToTurnException(exception));
        }
        finally
        {
            EndTurn();
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Applies changed configuration. An interval the scheduler rejects does not throw: the agent reports it as
    /// an error status and stops automatic runs until the configuration is corrected.
    /// </summary>
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _normalizedWatchPaths, AgentWatchPaths.Normalize(WatchPaths));
        if (_scheduler is { } scheduler)
        {
            var hadConfigurationError = _configurationError is not null;
            var wasPaused = scheduler.PauseReason is not null;
            if (!TryApplyIntervals(scheduler))
            {
                Status = ServiceStatus.Error;
                StatusMessage = _configurationError;
            }
            else if ((hadConfigurationError || (wasPaused && scheduler.PauseReason is null)) && !IsBusy)
            {
                SetIdle();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Executes one turn on the backend.</summary>
    protected abstract Task<AgentTurnResult> ExecuteTurnAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken);

    /// <summary>Discards the chat session of the backend.</summary>
    protected abstract Task ResetSessionAsync(CancellationToken cancellationToken);

    /// <summary>Called once storage is available, before the first turn; loads backend session state.</summary>
    protected virtual Task OnStartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Reads the backend's session file, or null when there is none.</summary>
    protected async Task<string?> ReadSessionAsync(CancellationToken cancellationToken)
    {
        var path = GetStoragePath(".session.json");
        return path is not null && File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    /// <summary>Writes the backend's session file atomically; null deletes it.</summary>
    protected async Task WriteSessionAsync(string? content, CancellationToken cancellationToken)
    {
        var path = GetStoragePath(".session.json");
        if (path is null)
        {
            return;
        }

        if (content is null)
        {
            File.Delete(path);
            return;
        }

        await AtomicFile.WriteAllTextAsync(path, content, cancellationToken);
    }

    /// <summary>Appends a divider, for example when a backend session had to be restarted.</summary>
    protected Task AppendDividerAsync(string text) => AppendAsync(new AgentTimelineEntry
    {
        Kind = AgentTimelineEntryKind.Divider,
        Timestamp = _services.TimeProvider.GetUtcNow(),
        Text = text
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status = ServiceStatus.Starting;
        StatusMessage = null;
        try
        {
            // Holding the gate keeps a chat turn from running against the timeline, memory and backend
            // session that are being replaced here.
            await _turnGate.WaitAsync(stoppingToken);
            try
            {
                await InitializeStorageAsync(stoppingToken);
                await OnStartingAsync(stoppingToken);
            }
            finally
            {
                _turnGate.Release();
            }

            Volatile.Write(ref _normalizedWatchPaths, AgentWatchPaths.Normalize(WatchPaths));

            var context = ((IInterceptorSubject)this).Context;
            var resolver = context.TryGetService<ISubjectPathResolver>();
            var scheduler = new AgentTriggerScheduler(_services.TimeProvider, pollInterval: null, TimeSpan.Zero, RunBackgroundAsync);
            TryApplyIntervals(scheduler);
            _scheduler = scheduler;

            using var processor = new ChangeQueueProcessor(
                this,
                context,
                IsWatchCandidate,
                (changes, _) => OnChangesAsync(changes, resolver),
                ChangeDeliveryRule.SourceValuesMayBeStale,
                bufferTime: ChangeBufferTime,
                maxQueueDepth: null,
                logger: _logger);

            SetIdle();
            await Task.WhenAll(scheduler.RunAsync(stoppingToken), processor.ProcessAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Status = ServiceStatus.Stopped;
            StatusMessage = null;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The agent {Agent} stopped after an unexpected error.", Title);
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
        }
    }

    public override void Dispose()
    {
        _turnGate.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Applies the configured intervals. When the scheduler rejects them, records the configuration error and
    /// disables automatic runs: polls stop here and change requests are dropped while the error is set.
    /// </summary>
    private bool TryApplyIntervals(AgentTriggerScheduler scheduler)
    {
        try
        {
            scheduler.Reconfigure(PollInterval, MinRunInterval);
            _configurationError = null;
            return true;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            _logger.LogError(exception, "The agent {Agent} has an invalid trigger configuration.", Title);
            _configurationError = "Invalid configuration: " + exception.Message;
            scheduler.Reconfigure(pollInterval: null, TimeSpan.Zero);
            return false;
        }
    }

    private async Task InitializeStorageAsync(CancellationToken cancellationToken)
    {
        var context = ((IInterceptorSubject)this).Context;
        var dataDirectory = context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        var path = context.TryGetService<ISubjectPathResolver>()?.GetPath(this, PathStyle.Canonical);
        if (dataDirectory is not null && path is not null)
        {
            _storageDirectory = Path.Combine(dataDirectory, "Agents");
            _storageName = AgentWatchPaths.GetStorageName(path);
            WorkingDirectory = Path.Combine(_storageDirectory, _storageName + ".workdir");
        }
        else
        {
            _logger.LogWarning("The agent {Agent} has no data directory or path; its timeline and memory are not persisted.", Title);
        }

        Timeline = new AgentTimeline(GetStoragePath(".timeline.jsonl"));
        Memory = new AgentMemory(GetStoragePath(".memory.md"), _services.TimeProvider);
        await Timeline.LoadAsync(cancellationToken);
        await Memory.LoadAsync(cancellationToken);
        RaiseTimelineChanged();
    }

    private string? GetStoragePath(string suffix) =>
        _storageDirectory is null ? null : Path.Combine(_storageDirectory, _storageName + suffix);

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
        if (resolver is null || watchPaths.Length == 0 || _configurationError is not null)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var change in changes.Span)
        {
            var subjectPath = resolver.GetPath(change.Property.Subject, PathStyle.Canonical);
            if (subjectPath is null)
            {
                continue;
            }

            var propertyPath = subjectPath == "/" ? "/" + change.Property.Name : subjectPath + "/" + change.Property.Name;
            if (AgentWatchPaths.Matches(propertyPath, watchPaths))
            {
                _scheduler?.RequestRun("change");
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    // The scheduler callback: an exception escaping it ends the scheduler loop, so only cancellation of the
    // stopping token may leave this method.
    private async Task RunBackgroundAsync(string reason, CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken);
        try
        {
            var started = BeginTurn("Running");
            var progress = CreateProgress();
            var tools = CreateToolSet(progress);
            var message = AgentPromptBuilder.BuildBackgroundMessage(
                reason,
                Timeline.GetRecentBackgroundResults(RecentResultCount),
                Timeline.GetLastQuietRunTime(),
                await FetchWatchedStateAsync(cancellationToken),
                started);

            var result = await RunTurnAsync(AgentTurnKind.Background, message, tools, progress, cancellationToken);
            var isQuiet = AgentPromptBuilder.IsQuiet(result.Text);
            await AppendAsync(new AgentTimelineEntry
            {
                Kind = AgentTimelineEntryKind.BackgroundRun,
                Timestamp = started,
                Text = result.Text,
                IsQuiet = isQuiet,
                Trigger = reason,
                ToolCalls = tools.Calls,
                Duration = _services.TimeProvider.GetUtcNow() - started,
                Usage = result.Usage
            });

            if (!isQuiet)
            {
                LastAnalysis = result.Text;
            }

            LastRunTime = started;
            RunCount++;
            _scheduler?.ReportSuccess();
            SetIdle();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await HandleFailureAsync(ToTurnException(exception));
            }
            catch (Exception handlerException)
            {
                _logger.LogError(handlerException, "The failure of agent {Agent} could not be recorded.", Title);
            }
        }
        finally
        {
            EndTurn();
            _turnGate.Release();
        }
    }

    private async Task<AgentTurnResult> RunTurnAsync(
        AgentTurnKind kind, string message, AgentToolSet tools, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        var isBackground = kind == AgentTurnKind.Background;
        var memory = Memory.GetSnapshot();
        var request = new AgentTurnRequest(
            kind,
            AgentPromptBuilder.BuildSystemPrompt(kind, Instructions, memory.Content, memory.LastUpdated),
            message,
            isBackground ? BackgroundModel ?? Model : Model,
            isBackground ? BackgroundEffort ?? Effort : Effort,
            tools);

        var turnTimeout = TurnTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (turnTimeout > TimeSpan.Zero && turnTimeout.TotalMilliseconds <= int.MaxValue)
        {
            timeout.CancelAfter(turnTimeout);
        }

        try
        {
            var result = await ExecuteTurnAsync(request, progress, timeout.Token);
            if (result.RateLimit is { } rateLimit)
            {
                FiveHourUsage = rateLimit.FiveHourUtilization;
                SevenDayUsage = rateLimit.SevenDayUtilization;
            }

            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AgentTurnException(AgentFailureKind.Transient, $"The turn did not finish within {turnTimeout}.");
        }
    }

    private async Task<IReadOnlyList<AgentWatchedState>> FetchWatchedStateAsync(CancellationToken cancellationToken)
    {
        var watchPaths = Volatile.Read(ref _normalizedWatchPaths);
        if (watchPaths.Length == 0)
        {
            return [];
        }

        var browse = _services.ToolSource.CreateTools(isReadOnly: true).FirstOrDefault(tool => tool.Name == "browse");
        var states = new List<AgentWatchedState>(watchPaths.Length);
        foreach (var path in watchPaths)
        {
            try
            {
                var input = JsonSerializer.SerializeToElement(new { path, depth = 2, includeProperties = true, format = "text" });
                var result = browse is null ? "(browse tool unavailable)" : await browse.Handler(input, cancellationToken);
                states.Add(new AgentWatchedState(path, result as string ?? JsonSerializer.Serialize(result)));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                states.Add(new AgentWatchedState(path, "Error: " + exception.Message));
            }
        }

        return states;
    }

    private AgentToolSet CreateToolSet(IProgress<AgentTurnUpdate> progress)
    {
        var allowedTools = AllowedTools;
        var available = _services.ToolSource.CreateTools(AgentToolSet.IsReadOnly(allowedTools));
        return AgentToolSet.Create(available, allowedTools, Memory.CreateUpdateTool(), MaxToolCallsPerTurn, progress);
    }

    private SynchronousProgress<AgentTurnUpdate> CreateProgress() => new(update =>
    {
        if (update is AgentTextDelta delta)
        {
            lock (_streamingLock)
            {
                _streamingText.Append(delta.Text);
            }
        }

        RaiseTimelineChanged();
    });

    private DateTimeOffset BeginTurn(string statusMessage)
    {
        lock (_streamingLock)
        {
            _streamingText.Clear();
        }

        IsBusy = true;
        Status = ServiceStatus.Running;
        StatusMessage = statusMessage;
        return _services.TimeProvider.GetUtcNow();
    }

    private void EndTurn()
    {
        lock (_streamingLock)
        {
            _streamingText.Clear();
        }

        IsBusy = false;
        RaiseTimelineChanged();
    }

    private void SetIdle()
    {
        if (_configurationError is { } configurationError)
        {
            Status = ServiceStatus.Error;
            StatusMessage = configurationError;
            return;
        }

        var pauseReason = _scheduler?.PauseReason;
        Status = pauseReason is null ? ServiceStatus.Running : ServiceStatus.Unavailable;
        StatusMessage = pauseReason ?? "Idle";
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

    private async Task HandleFailureAsync(AgentTurnException exception)
    {
        _logger.LogWarning("The turn of agent {Agent} failed: {Message}", Title, exception.Message);
        _scheduler?.ReportFailure(exception);

        // Recorded before the status changes, so a reader that sees the failure status also sees the entry.
        await AppendAsync(new AgentTimelineEntry
        {
            Kind = AgentTimelineEntryKind.Error,
            Timestamp = _services.TimeProvider.GetUtcNow(),
            Text = exception.Message
        });

        Status = exception.Kind is AgentFailureKind.Configuration or AgentFailureKind.UsageLimit
            ? ServiceStatus.Unavailable
            : ServiceStatus.Error;
        StatusMessage = exception.Message;
    }

    private async Task AppendAsync(AgentTimelineEntry entry)
    {
        try
        {
            // Not cancellable: an entry of a finished turn is written even while the host stops.
            await Timeline.AppendAsync(entry, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The entry is already in memory; a failed write must not fail the turn or the scheduler loop.
            _logger.LogWarning(exception, "The timeline of agent {Agent} could not be written.", Title);
        }

        RaiseTimelineChanged();
    }

    private void RaiseTimelineChanged()
    {
        try
        {
            TimelineChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A timeline subscriber of agent {Agent} failed.", Title);
        }
    }
}
