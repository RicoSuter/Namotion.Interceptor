using System.Text;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Base of the built-in agents: configuration, background triggers, chat, memory, timeline and tool scoping.
/// A subclass only executes a turn on its backend and resets its chat session.
/// </summary>
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "The agent base coordinates configuration, triggers, the turn gate, prompts, memory, timeline and tool scoping around one lifecycle and state lock; logic that needs no agent state lives in helper types, so splitting the class would spread that shared state.")]
public abstract partial class LlmAgentBase : BackgroundService, IConfigurable, IMonitoredService, ITitleProvider, IIconProvider
{
    /// <summary>The text of the error entry recorded for a chat message that was cancelled before a reply.</summary>
    public const string CancelledText = "Cancelled before a reply.";

    private const string IdleStatusMessage = "Idle";

    /// <summary>The status message while a chat turn runs.</summary>
    public const string AnsweringStatusMessage = "Answering";

    /// <summary>The status message while a background run runs.</summary>
    public const string BackgroundRunStatusMessage = "Running";

    /// <summary>The status message while a chat message waits for a background run or the previous chat turn to end.</summary>
    public const string WaitingStatusMessage = "Waiting for the previous turn to finish";

    /// <summary>The message of the exception thrown when a chat message is sent while the agent is not running.</summary>
    public const string NotRunningText = "The agent is not running.";

    /// <summary>The longest <see cref="TurnTimeout"/> that applies; a longer one means no limit.</summary>
    public static readonly TimeSpan MaximumTurnTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private const int ExtraModelRounds = 10;
    private static readonly TimeSpan ChangeBufferTime = TimeSpan.FromMilliseconds(100);

    private readonly AgentServices _services;
    private readonly ILogger _logger;
    // Never disposed: its wait handle is never created, and a chat that unwinds after the agent was disposed still
    // releases it.
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly Lock _streamingLock = new();
    private readonly StringBuilder _streamingText = new();

    // Guards _scheduler, _lifetime, _runGeneration, the configuration error, the configuration versions, the
    // pending idle report and every status write of a started agent, so a turn that ends after the agent stopped
    // cannot report it as running. A non-null _scheduler means started. Status and StatusMessage are intercepted
    // property writes, so change subscribers run while this lock is held and must neither block nor call back into
    // the agent synchronously.
    private readonly Lock _stateLock = new();
    private AgentTriggerScheduler? _scheduler;
    private CancellationTokenSource? _lifetime;
    private int _runGeneration;
    private int _configurationVersion;
    private int _turnConfigurationVersion;
    private bool _isIdleReportPending;
    private string[] _normalizedWatchPaths = [];
    private volatile string? _configurationError;
    private string? _storageDirectory;
    private string? _storageName;
    private volatile bool _isStorageLoaded;

    /// <summary>
    /// Initializes the agent; <paramref name="supportedBackendTools"/> are the backend tools its type can run, none
    /// when null.
    /// </summary>
    protected LlmAgentBase(AgentServices services, ILogger logger, IReadOnlyList<AgentBackendTool>? supportedBackendTools = null)
    {
        _services = services;
        _logger = logger;
        SupportedBackendTools = supportedBackendTools ?? [];
        Timeline = new AgentTimeline(filePath: null);
        Memory = new AgentMemory(filePath: null, services.TimeProvider, logger);
        Memory.Changed += RaiseStateChanged;
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

    /// <summary>
    /// The time a turn may take. Zero or less, or more than <see cref="MaximumTurnTimeout"/> (about 24.8 days), means no
    /// limit; see <see cref="GetEffectiveTurnTimeout"/>.
    /// </summary>
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

    /// <summary>
    /// The start of the newest turn, chat or background, also of one that failed or was cancelled. Kept across
    /// restarts and new conversations.
    /// </summary>
    [State("Last Activity", Position = 5)]
    public partial DateTimeOffset? LastActivityTime { get; internal set; }

    /// <summary>The start of the newest background run, also of one that failed or was cancelled; chat turns do not count.</summary>
    [State("Last Background Run", Position = 6)]
    public partial DateTimeOffset? LastRunTime { get; internal set; }

    /// <summary>
    /// The number of background runs, also those that failed or were cancelled; chat turns do not count. Kept across
    /// restarts and new conversations; deleting the agent's data files resets it.
    /// </summary>
    [State("Background Runs", Position = 7)]
    public partial int RunCount { get; internal set; }

    [State("Usage (5 hours)", Position = 8)]
    public partial double? FiveHourUsage { get; internal set; }

    [State("Usage (7 days)", Position = 9)]
    public partial double? SevenDayUsage { get; internal set; }

    [Derived]
    public string? Title => Name ?? GetType().Name;

    public string? IconName => "SmartToy";

    /// <summary>
    /// The backend tools this agent type can run, which are enabled by adding their names to <see cref="AllowedTools"/>.
    /// </summary>
    public IReadOnlyList<AgentBackendTool> SupportedBackendTools { get; }

    /// <summary>The timeline of background runs and chat.</summary>
    public AgentTimeline Timeline { get; private set; }

    /// <summary>The agent's memory entries.</summary>
    public AgentMemory Memory { get; private set; }

    /// <summary>
    /// Whether the agent has loaded its timeline and memory, which happens when it starts for the first time. Before
    /// that, <see cref="Timeline"/> and <see cref="Memory"/> are empty placeholders, which the start replaces unless
    /// the agent has no storage and keeps them.
    /// </summary>
    public bool IsStorageLoaded => _isStorageLoaded;

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
    /// Raised on the changing thread when the timeline, the memory, <see cref="Status"/>, <see cref="StatusMessage"/>,
    /// <see cref="IsBusy"/> or the streamed text of the running turn changes. Handlers may run while the agent holds
    /// its state lock, so they must not block. Exceptions thrown by handlers are logged and swallowed.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>The working directory for external processes of this agent.</summary>
    protected string WorkingDirectory { get; private set; } = Path.Combine(Path.GetTempPath(), "homeblaze-agent-" + Guid.NewGuid().ToString("N"));

    /// <summary>The services shared by agents.</summary>
    protected AgentServices Services => _services;

    /// <summary>The turn timeout that applies, or null for no limit.</summary>
    public TimeSpan? GetEffectiveTurnTimeout()
    {
        var turnTimeout = TurnTimeout;
        return turnTimeout > TimeSpan.Zero && turnTimeout <= MaximumTurnTimeout ? turnTimeout : null;
    }

    /// <summary>The minimum run interval that applies: <see cref="MinRunInterval"/>, or zero when it is negative.</summary>
    public TimeSpan GetEffectiveMinRunInterval()
    {
        var minRunInterval = MinRunInterval;
        return minRunInterval > TimeSpan.Zero ? minRunInterval : TimeSpan.Zero;
    }

    /// <summary>
    /// Applies changed configuration. An interval the scheduler rejects does not throw: the agent reports it as
    /// an error status and stops automatic runs until the configuration is corrected.
    /// </summary>
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _configurationVersion++;
            Volatile.Write(ref _normalizedWatchPaths, AgentWatchPaths.Normalize(WatchPaths));
            if (_scheduler is not { } scheduler)
            {
                return Task.CompletedTask;
            }

            var hadConfigurationError = _configurationError is not null;
            var previousPauseReason = scheduler.PauseReason;
            if (!TryApplyIntervals(scheduler))
            {
                Status = ServiceStatus.Error;
                StatusMessage = _configurationError;
            }
            else if (hadConfigurationError || !string.Equals(previousPauseReason, scheduler.PauseReason, StringComparison.Ordinal))
            {
                if (IsBusy)
                {
                    // The running turn writes its own status until it ends, and reports this change then.
                    _isIdleReportPending = true;
                }
                else
                {
                    SetIdle();
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The model rounds a turn may take: <see cref="MaxToolCallsPerTurn"/> plus a fixed margin, so a turn whose tool
    /// budget ran out still has rounds to read the rejected calls and answer.
    /// </summary>
    protected int GetModelRoundLimit() => (int)Math.Min((long)Math.Max(MaxToolCallsPerTurn, 0) + ExtraModelRounds, int.MaxValue);

    /// <summary>Executes one turn on the backend.</summary>
    protected abstract Task<AgentTurnResult> ExecuteTurnAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken);

    /// <summary>Discards the chat session of the backend.</summary>
    protected abstract Task ResetSessionAsync(CancellationToken cancellationToken);

    /// <summary>Called once storage is available, before the first turn; loads backend session state.</summary>
    protected virtual Task OnStartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Called after the answer of a chat turn is in the timeline and the agent is no longer busy, after
    /// <see cref="SendMessageAsync"/> returned, before the next turn can start and outside the turn timeout; for backend
    /// upkeep such as summarizing the chat session. A stop of the agent waits for it to end.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the agent stops.</param>
    /// <remarks>An exception is logged as a warning, so it must not carry secrets, and does not change the recorded turn.</remarks>
    protected virtual Task OnChatTurnCompletedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Reads the backend's session file and parses it with <paramref name="parse"/>, which may throw
    /// <see cref="JsonException"/>; returns null when there is no file or it cannot be read or parsed. Failures are
    /// logged, so a damaged file only loses the chat context.
    /// </summary>
    protected Task<T?> ReadSessionAsync<T>(Func<string, T?> parse, CancellationToken cancellationToken)
        where T : class =>
        AgentStorage.TryReadJsonDataFileAsync(GetStoragePath(AgentStorage.SessionSuffix), parse, "a new session starts", _logger, this, cancellationToken);

    /// <summary>
    /// Writes the backend's session file atomically; null deletes it. Write failures are logged, not thrown, so the
    /// file may keep its previous content.
    /// </summary>
    protected Task WriteSessionAsync(string? content, CancellationToken cancellationToken) =>
        AgentStorage.TryWriteDataFileAsync(
            GetStoragePath(AgentStorage.SessionSuffix), content, "after a restart the agent may resume the previous session", _logger, this, cancellationToken);

    /// <summary>Appends a divider, for example when a backend session had to be restarted.</summary>
    protected Task AppendDividerAsync(string text) => AppendAsync(new AgentTimelineEntry
    {
        Kind = AgentTimelineEntryKind.Divider,
        Timestamp = _services.TimeProvider.GetUtcNow(),
        Text = text
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int generation;
        lock (_stateLock)
        {
            generation = ++_runGeneration;
            // A run that outlived its stop may still have published its cancelled lifetime and scheduler; until this
            // run publishes its own, the agent is not started, so chat, manual runs and idle reports are refused.
            _scheduler = null;
            _lifetime = null;
            Status = ServiceStatus.Starting;
            StatusMessage = null;
        }

        // Cancelled when this method ends for any reason; chat turns link to it so they end with the agent.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var finalStatus = ServiceStatus.Stopped;
        string? finalStatusMessage = null;
        try
        {
            await RunAsync(lifetime, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A requested stop; the agent ends as stopped.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The agent {Agent} stopped after an unexpected error.", Title);
            finalStatus = ServiceStatus.Error;
            finalStatusMessage = exception.Message;
        }

        await lifetime.CancelAsync();

        // Chat turns and their upkeep hold the gate and are cancelled with the lifetime above; waited for, so that
        // none runs on after the agent stopped. A turn that ignores the cancellation keeps the stop waiting, as a
        // background run does. A chat still waiting for the gate sees the cancelled lifetime and only appends its
        // cancelled entry, without the gate.
        await WaitForTurnGateAsync();
        lock (_stateLock)
        {
            // A run that outlived its stop, after a restart already began a newer one, must not clear or
            // overwrite the newer run's state; this also holds when the old run never published a scheduler.
            if (generation == _runGeneration)
            {
                _scheduler = null;
                _lifetime = null;
                Status = finalStatus;
                StatusMessage = finalStatusMessage;
            }
        }
    }

    /// <summary>Waits until no turn, upkeep or start holds the turn gate; it never faults.</summary>
    internal async Task WaitForTurnGateAsync()
    {
        await _turnGate.WaitAsync(CancellationToken.None);
        _turnGate.Release();
    }

    // A configuration file may hold an explicit null, which the rest of the agent never expects.
    partial void OnWatchPathsChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    partial void OnAllowedToolsChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    partial void OnStatusChanged(ServiceStatus newValue) => RaiseStateChanged();

    partial void OnStatusMessageChanged(string? newValue) => RaiseStateChanged();

    partial void OnIsBusyChanged(bool newValue) => RaiseStateChanged();

    private async Task RunAsync(CancellationTokenSource lifetime, CancellationToken stoppingToken)
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

        var context = ((IInterceptorSubject)this).Context;
        var resolver = context.TryGetService<ISubjectPathResolver>();
        var scheduler = new AgentTriggerScheduler(_services.TimeProvider, pollInterval: null, TimeSpan.Zero, RunBackgroundAsync, OnPauseLifted);
        using var processor = new ChangeQueueProcessor(
            this,
            context,
            IsWatchCandidate,
            (changes, _) => OnChangesAsync(changes, resolver),
            ChangeDeliveryRule.SourceValuesMayBeStale,
            bufferTime: ChangeBufferTime,
            maxQueueDepth: null,
            logger: _logger);

        // Published before the configuration is read, under the lock ApplyConfigurationAsync takes, so a
        // concurrent configuration change is either read here or applied to this scheduler.
        lock (_stateLock)
        {
            // A run stopped before publishing must not publish over a newer run started meanwhile.
            stoppingToken.ThrowIfCancellationRequested();
            _scheduler = scheduler;
            _lifetime = lifetime;
            Volatile.Write(ref _normalizedWatchPaths, AgentWatchPaths.Normalize(WatchPaths));
            TryApplyIntervals(scheduler);
            SetIdle();
        }

        var schedulerTask = scheduler.RunAsync(lifetime.Token);
        var processorTask = processor.ProcessAsync(lifetime.Token);
        var completed = await Task.WhenAny(schedulerTask, processorTask);

        // Either loop ending stops the other, so a fault in one does not leave a half-working agent.
        await lifetime.CancelAsync();
        await WaitForLoopEndAsync(completed == schedulerTask ? processorTask : schedulerTask);
        await completed;
        if (!stoppingToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(completed == schedulerTask
                ? "The trigger loop ended unexpectedly."
                : "The change processing loop ended unexpectedly.");
        }
    }

    private async Task WaitForLoopEndAsync(Task loop)
    {
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // The loop ended through the cancellation that stops it.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A loop of agent {Agent} failed while stopping.", Title);
        }
    }

    /// <summary>
    /// Applies the configured intervals; the caller holds <see cref="_stateLock"/>. When the scheduler rejects them,
    /// records the configuration error and disables automatic runs: polls stop here and change requests are
    /// dropped while the error is set.
    /// </summary>
    private bool TryApplyIntervals(AgentTriggerScheduler scheduler)
    {
        try
        {
            scheduler.Reconfigure(PollInterval, GetEffectiveMinRunInterval());
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
        var resolver = context.TryGetService<ISubjectPathResolver>();
        var path = resolver?.GetPath(this, PathStyle.Canonical);
        if (dataDirectory is not null && path is not null)
        {
            _storageDirectory = Path.Combine(dataDirectory, "Agents");
            _storageName = AgentStorage.GetName(path);
            WorkingDirectory = Path.Combine(_storageDirectory, _storageName + AgentStorage.WorkingDirectorySuffix);
        }
        else
        {
            _logger.LogWarning("The agent {Agent} has no data directory or path; its timeline and memory are not persisted.", Title);
        }

        await LoadTimelineAsync(cancellationToken);

        // Loaded before it is published, so the memory editor never writes to a memory that has not read its file.
        // A restart keeps the instance of an unchanged path, also of none, so two instances never write the same file
        // and a memory without storage keeps its entries.
        var memoryPath = GetStoragePath(AgentStorage.MemorySuffix);
        var memory = Memory.FilePath == memoryPath
            ? Memory
            : new AgentMemory(memoryPath, _services.TimeProvider, _logger);
        await memory.LoadAsync(cancellationToken);
        if (!ReferenceEquals(memory, Memory))
        {
            Memory.Changed -= RaiseStateChanged;
            memory.Changed += RaiseStateChanged;
            Memory = memory;
        }

        await RestoreRunStatisticsAsync(cancellationToken);
        LastAnalysis = Timeline.GetRecentBackgroundResults(1) is [var newestResult] ? newestResult.ResultText : null;
        _isStorageLoaded = true;
        RaiseStateChanged();
    }

    private async Task LoadTimelineAsync(CancellationToken cancellationToken)
    {
        // Loaded before it is published, so a reader never sees an empty placeholder. A restart keeps the instance of
        // an unchanged path, also of none, so an append of the stopped run and this load share its file lock, or the
        // append lands in the timeline that the agent shows when it has no storage.
        var timelinePath = GetStoragePath(AgentStorage.TimelineSuffix);
        var timeline = Timeline.FilePath == timelinePath
            ? Timeline
            : new AgentTimeline(timelinePath);
        await timeline.LoadAsync(cancellationToken);
        Timeline = timeline;
    }

    private string? GetStoragePath(string suffix) =>
        _storageDirectory is null ? null : Path.Combine(_storageDirectory, _storageName + suffix);

    /// <summary>
    /// Persists the run count and the last activity, also while the host stops; the caller holds the turn gate.
    /// </summary>
    private Task WriteRunStatisticsAsync() => AgentStorage.TryWriteDataFileAsync(
        GetStoragePath(AgentStorage.StatsSuffix),
        JsonSerializer.Serialize(new AgentStats(RunCount, LastActivityTime), AgentJsonContext.Default.AgentStats),
        "after a restart the run count and the last activity may be older",
        _logger,
        this,
        CancellationToken.None);

    /// <summary>
    /// Restores the run count and the last activity from the stats file, none when it is missing, and the last run
    /// from the timeline; the caller holds the turn gate.
    /// </summary>
    private async Task RestoreRunStatisticsAsync(CancellationToken cancellationToken)
    {
        var statistics = await AgentStorage.TryReadJsonDataFileAsync(
            GetStoragePath(AgentStorage.StatsSuffix),
            content => JsonSerializer.Deserialize(content, AgentJsonContext.Default.AgentStats),
            "the run count starts at zero and the last activity is unknown",
            _logger,
            this,
            cancellationToken);
        LastRunTime = Timeline.GetLastBackgroundRunTime();
        RunCount = statistics?.BackgroundRunCount ?? 0;
        LastActivityTime = statistics?.LastActivityTime;
    }

    private void OnPauseLifted()
    {
        lock (_stateLock)
        {
            // The failure of a turn that ended during the pause replaced the pause status and stays until the next
            // turn. A pause status while busy belongs to a failed turn that has not ended yet, or to a new turn that
            // has not written its running status yet; either reports the end of the pause when it ends.
            if (Status != ServiceStatus.Unavailable)
            {
                return;
            }

            if (IsBusy)
            {
                _isIdleReportPending = true;
            }
            else
            {
                SetIdle();
            }
        }
    }

    /// <summary>
    /// Reports the idle status of a started agent; a stopped, starting or stopping agent keeps its status.
    /// </summary>
    private void SetIdle()
    {
        lock (_stateLock)
        {
            // A stopping agent still has its scheduler until the turns it waits for have ended.
            if (_scheduler is not { } scheduler || _lifetime is { IsCancellationRequested: true })
            {
                return;
            }

            if (_configurationError is { } configurationError)
            {
                Status = ServiceStatus.Error;
                StatusMessage = configurationError;
                return;
            }

            var pauseReason = scheduler.PauseReason;
            Status = pauseReason is null ? ServiceStatus.Running : ServiceStatus.Unavailable;
            StatusMessage = pauseReason ?? IdleStatusMessage;
        }
    }

    private void SetStatusIfStarted(ServiceStatus status, string? statusMessage)
    {
        lock (_stateLock)
        {
            if (_scheduler is not null)
            {
                Status = status;
                StatusMessage = statusMessage;
            }
        }
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

        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A state subscriber of agent {Agent} failed.", Title);
        }
    }
}

internal sealed record AgentStats(int BackgroundRunCount, DateTimeOffset? LastActivityTime);
