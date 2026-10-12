using System.Text;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Base of the built-in agents: configuration, background triggers, chat, memory, timeline and tool scoping.
/// A subclass only executes a turn on its backend and resets its chat session.
/// </summary>
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "The agent base coordinates configuration, triggers, the turn gate, prompts, memory, timeline and tool scoping around one lifecycle and state lock; the logic already lives in dedicated helper types, so splitting further would spread that shared state.")]
public abstract partial class LlmAgentBase : BackgroundService, IConfigurable, IMonitoredService, ITitleProvider, IIconProvider
{
    /// <summary>The text of the error entry recorded for a chat message that was cancelled before a reply.</summary>
    public const string CancelledText = "Cancelled before a reply.";

    private const string IdleStatusMessage = "Idle";

    /// <summary>The status message while a chat turn runs.</summary>
    public const string AnsweringStatusMessage = "Answering";

    /// <summary>The status message while a background run runs.</summary>
    public const string BackgroundRunStatusMessage = "Running";

    /// <summary>The status message while a chat message waits for a background run to end.</summary>
    public const string WaitingStatusMessage = "Waiting for the background run";

    /// <summary>The message of the exception thrown when a chat message is sent while the agent is not running.</summary>
    public const string NotRunningText = "The agent is not running.";

    /// <summary>The longest <see cref="TurnTimeout"/> that applies; a longer one means no limit.</summary>
    public static readonly TimeSpan MaximumTurnTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private const int RecentResultCount = 5;
    private static readonly TimeSpan ChangeBufferTime = TimeSpan.FromMilliseconds(100);

    // The agent whose turn the current async flow belongs to, so re-entering the turn gate fails fast.
    private static readonly AsyncLocal<LlmAgentBase?> CurrentTurnAgent = new();

    private readonly AgentServices _services;
    private readonly ILogger _logger;
    // Never disposed: its wait handle is never created, and a chat that unwinds after the agent was disposed still
    // releases it.
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly Lock _streamingLock = new();
    private readonly StringBuilder _streamingText = new();

    // Guards _scheduler, _lifetime, _runGeneration, the configuration error and every status write of a started
    // agent, so a turn that ends after the agent stopped cannot report it as running. A non-null _scheduler means
    // started. Status and StatusMessage are intercepted property writes, so change subscribers run while this lock
    // is held and must neither block nor call back into the agent synchronously.
    private readonly Lock _stateLock = new();
    private AgentTriggerScheduler? _scheduler;
    private CancellationTokenSource? _lifetime;
    private int _runGeneration;
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
    /// that, <see cref="Timeline"/> and <see cref="Memory"/> are empty placeholders that are replaced on start.
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
    /// Raised when the timeline or the streaming state of the running turn changes, on the changing thread.
    /// Exceptions thrown by handlers are logged and swallowed.
    /// </summary>
    public event Action? TimelineChanged;

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

    // A configuration file may hold an explicit null, which the rest of the agent never expects.
    partial void OnWatchPathsChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    partial void OnAllowedToolsChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];

    /// <summary>Starts a background run now, also while automatic runs are paused.</summary>
    [Operation(Title = "Run Now", Icon = "PlayArrow", Position = 1)]
    public void RunNow()
    {
        lock (_stateLock)
        {
            _scheduler?.RequestManualRun();
        }
    }

    /// <summary>Discards the chat session; memory and timeline stay.</summary>
    /// <remarks>
    /// Throws <see cref="InvalidOperationException"/> when called from within a turn of this agent, for example
    /// through a tool call, because the turn holds the gate this method waits for.
    /// </remarks>
    [Operation(Title = "New Conversation", Icon = "RestartAlt", Position = 2, RequiresConfirmation = true)]
    public async Task NewConversationAsync(CancellationToken cancellationToken)
    {
        if (CurrentTurnAgent.Value == this)
        {
            throw new InvalidOperationException("A new conversation cannot be started from within a turn of the same agent.");
        }

        await _turnGate.WaitAsync(cancellationToken);
        try
        {
            await ResetSessionAsync(cancellationToken);
            await AppendDividerAsync("New conversation");
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Sends a chat message and waits for the reply, which is appended to the timeline. Failures are recorded in
    /// the timeline instead of thrown. A message cancelled through <paramref name="cancellationToken"/> or by the
    /// agent stopping is recorded as an error entry with <see cref="CancelledText"/>, so background findings
    /// stay pending for the next message; only cancellation through <paramref name="cancellationToken"/> throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">The agent is not running; the message is not recorded.</exception>
    public async Task SendMessageAsync(string text, CancellationToken cancellationToken)
    {
        CancellationTokenSource? turnCancellation = null;
        lock (_stateLock)
        {
            if (_lifetime is not null)
            {
                // Linked under the lock: ExecuteAsync disposes the lifetime source only after clearing it here.
                turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            }
        }

        // Checked before the message is recorded: before the first start the timeline is a placeholder that the
        // stored timeline replaces.
        if (turnCancellation is null)
        {
            throw new InvalidOperationException(NotRunningText);
        }

        using (turnCancellation)
        {
            await AppendAsync(new AgentTimelineEntry
            {
                Kind = AgentTimelineEntryKind.User,
                Timestamp = _services.TimeProvider.GetUtcNow(),
                Text = text
            });

            var turnToken = turnCancellation.Token;
            if (!await TryEnterGateForChatAsync(turnToken))
            {
                await AppendErrorAsync(CancelledText);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            var isIdle = false;
            try
            {
                var started = BeginTurn(AnsweringStatusMessage);
                await WriteRunStatisticsAsync();
                var progress = CreateProgress();
                var tools = CreateToolSet(progress);
                var message = AgentPromptBuilder.BuildChatMessage(text, Timeline.GetFindingsSinceLastReply());
                var result = await RunTurnAsync(AgentTurnKind.Chat, message, tools, progress, turnToken);
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
                lock (_stateLock)
                {
                    _scheduler?.ReportSuccess();
                }

                isIdle = true;
            }
            catch (OperationCanceledException) when (turnToken.IsCancellationRequested)
            {
                await AppendErrorAsync(CancelledText);
                isIdle = true;
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                await HandleFailureAsync(ToTurnException(exception));
            }
            finally
            {
                EndTurn(isIdle);
                _turnGate.Release();
            }
        }
    }

    /// <summary>
    /// Applies changed configuration. An interval the scheduler rejects does not throw: the agent reports it as
    /// an error status and stops automatic runs until the configuration is corrected.
    /// </summary>
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            Volatile.Write(ref _normalizedWatchPaths, AgentWatchPaths.Normalize(WatchPaths));
            if (_scheduler is not { } scheduler)
            {
                return Task.CompletedTask;
            }

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

    /// <summary>
    /// Reads the backend's session file and parses it with <paramref name="parse"/>, which may throw
    /// <see cref="JsonException"/>; returns null when there is no file or it cannot be read or parsed. Failures are
    /// logged, so a damaged file only loses the chat context.
    /// </summary>
    protected async Task<T?> ReadSessionAsync<T>(Func<string, T?> parse, CancellationToken cancellationToken)
        where T : class
    {
        var content = await TryReadDataFileAsync(AgentStorage.SessionSuffix, SessionReadConsequence, cancellationToken);
        try
        {
            return content is null ? null : parse(content);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "The session file of agent {Agent} could not be read; {Consequence}.", Title, SessionReadConsequence);
            return null;
        }
    }

    /// <summary>
    /// Writes the backend's session file atomically; null deletes it. Write failures are logged, not thrown, so the
    /// file may keep its previous content.
    /// </summary>
    protected Task WriteSessionAsync(string? content, CancellationToken cancellationToken) =>
        TryWriteDataFileAsync(AgentStorage.SessionSuffix, content, "after a restart the agent may resume the previous session", cancellationToken);

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
            AgentStorage.MigrateLegacyName(
                _storageDirectory, path, otherPath => resolver!.ResolveSubject(otherPath, PathStyle.Canonical) is not null, _logger);
            WorkingDirectory = Path.Combine(_storageDirectory, _storageName + AgentStorage.WorkingDirectorySuffix);
        }
        else
        {
            _logger.LogWarning("The agent {Agent} has no data directory or path; its timeline and memory are not persisted.", Title);
        }

        Timeline = new AgentTimeline(GetStoragePath(AgentStorage.TimelineSuffix));
        await Timeline.LoadAsync(cancellationToken);

        // Loaded before it is published, so the memory editor never writes to a memory that has not read its file.
        // A restart keeps the instance of an unchanged path, so two instances never write the same file.
        var memoryPath = GetStoragePath(AgentStorage.MemorySuffix);
        var memory = memoryPath is not null && Memory.FilePath == memoryPath
            ? Memory
            : new AgentMemory(memoryPath, _services.TimeProvider, _logger, legacyFilePath: GetStoragePath(AgentStorage.LegacyMemorySuffix));
        await memory.LoadAsync(cancellationToken);
        Memory = memory;
        await RestoreRunStatisticsAsync(cancellationToken);
        LastAnalysis = Timeline.GetRecentBackgroundResults(1) is [var newestResult] ? newestResult.ResultText : null;
        _isStorageLoaded = true;
        RaiseTimelineChanged();
    }

    private const string SessionReadConsequence = "a new session starts";
    private const string StatsReadConsequence = "the run statistics are taken from the timeline";

    private string? GetStoragePath(string suffix) =>
        _storageDirectory is null ? null : Path.Combine(_storageDirectory, _storageName + suffix);

    /// <summary>
    /// Reads a data file of this agent, or null when the agent has no storage, the file does not exist or it cannot be
    /// read; read failures are logged.
    /// </summary>
    /// <param name="suffix">The suffix of the file name.</param>
    /// <param name="consequence">What happens when the file cannot be read, for the warning.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task<string?> TryReadDataFileAsync(string suffix, string consequence, CancellationToken cancellationToken)
    {
        var path = GetStoragePath(suffix);
        if (path is null)
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The file {Path} of agent {Agent} could not be read; {Consequence}.", path, Title, consequence);
            return null;
        }
    }

    /// <summary>
    /// Writes a data file of this agent atomically, or deletes it for null content; does nothing when the agent has no
    /// storage. Write failures are logged, not thrown.
    /// </summary>
    /// <param name="suffix">The suffix of the file name.</param>
    /// <param name="content">The content, or null to delete the file.</param>
    /// <param name="consequence">What happens when the file cannot be written, for the warning.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task TryWriteDataFileAsync(string suffix, string? content, string consequence, CancellationToken cancellationToken)
    {
        var path = GetStoragePath(suffix);
        if (path is null)
        {
            return;
        }

        try
        {
            if (content is null)
            {
                File.Delete(path);
                return;
            }

            await AtomicFile.WriteAllTextAsync(path, content, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The file {Path} of agent {Agent} could not be written; {Consequence}.", path, Title, consequence);
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
        var isCounted = false;
        var isIdle = false;
        try
        {
            started = BeginTurn(BackgroundRunStatusMessage);
            var progress = CreateProgress();
            var tools = CreateToolSet(progress);
            var message = AgentPromptBuilder.BuildBackgroundMessage(
                reason,
                Timeline.GetRecentBackgroundResults(RecentResultCount),
                Timeline.GetLastQuietRunTime(),
                await FetchWatchedStateAsync(cancellationToken),
                started.Value);

            var result = await RunTurnAsync(AgentTurnKind.Background, message, tools, progress, cancellationToken);
            var isQuiet = AgentPromptBuilder.IsQuiet(result.FinalText);
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
                await AppendAsync(CreateBackgroundErrorEntry(CancelledText, reason, cancelledStart));
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
                    await HandleFailureAsync(turnException, CreateBackgroundErrorEntry(turnException.Message, reason, failedStart));
                }
                else
                {
                    await HandleFailureAsync(turnException);
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

    private AgentTimelineEntry CreateBackgroundErrorEntry(string text, string trigger, DateTimeOffset started) => new()
    {
        Kind = AgentTimelineEntryKind.Error,
        Timestamp = started,
        Text = text,
        Trigger = trigger,
        Duration = _services.TimeProvider.GetUtcNow() - started
    };

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
    /// Restores the run count and the last activity from the stats file, or from the timeline when the file or the
    /// value is missing, and the last run from the timeline; the caller holds the turn gate.
    /// </summary>
    private async Task RestoreRunStatisticsAsync(CancellationToken cancellationToken)
    {
        var statistics = await ReadRunStatisticsAsync(cancellationToken);
        LastRunTime = Timeline.GetLastBackgroundRunTime();
        RunCount = statistics?.BackgroundRunCount ?? Timeline.Entries.Count(AgentTimeline.IsBackgroundRun);
        LastActivityTime = statistics?.LastActivityTime ?? Timeline.GetLastActivityTime();
    }

    private async Task<AgentStats?> ReadRunStatisticsAsync(CancellationToken cancellationToken)
    {
        var content = await TryReadDataFileAsync(AgentStorage.StatsSuffix, StatsReadConsequence, cancellationToken);
        try
        {
            return content is null ? null : JsonSerializer.Deserialize(content, AgentJsonContext.Default.AgentStats);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "The stats file of agent {Agent} could not be read; {Consequence}.", Title, StatsReadConsequence);
            return null;
        }
    }

    private async Task<AgentTurnResult> RunTurnAsync(
        AgentTurnKind kind, string message, AgentToolSet tools, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        var isBackground = kind == AgentTurnKind.Background;
        var allowedTools = AllowedTools;
        var backendTools = SupportedBackendTools.Where(tool => allowedTools.Contains(tool.Name)).ToList();
        var request = new AgentTurnRequest(
            kind,
            AgentPromptBuilder.BuildSystemPrompt(kind, Instructions, Memory.GetSnapshot(), backendTools.Exists(tool => tool.LoadsWebContent)),
            message,
            isBackground ? BackgroundModel ?? Model : Model,
            isBackground ? BackgroundEffort ?? Effort : Effort,
            tools)
        {
            BackendTools = backendTools.ConvertAll(tool => tool.Name)
        };

        var turnTimeout = GetEffectiveTurnTimeout();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (turnTimeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        try
        {
            // Flows into tool calls made in-process; tool calls arriving on another flow are marked by CreateToolSet.
            CurrentTurnAgent.Value = this;
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

    private AgentToolSet CreateToolSet(IProgress<AgentTurnUpdate> progress)
    {
        var allowedTools = AllowedTools;
        var available = _services.ToolSource.CreateTools(AgentToolSet.IsReadOnly(allowedTools))
            .Where(tool => allowedTools.Contains(tool.Name))
            .Select(MarkAsTurnTool)
            .ToList();
        return AgentToolSet.Create(available, allowedTools, Memory.CreateUpdateTool(), MaxToolCallsPerTurn, progress);
    }

    // A backend may call tools on a flow that does not descend from RunTurnAsync, such as an HTTP request of
    // the MCP route, so each call marks its own flow as part of this agent's turn.
    private McpToolInfo MarkAsTurnTool(McpToolInfo tool) => new(tool)
    {
        Handler = async (input, cancellationToken) =>
        {
            CurrentTurnAgent.Value = this;
            return await tool.Handler(input, cancellationToken);
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

        RaiseTimelineChanged();
    });

    private DateTimeOffset BeginTurn(string statusMessage)
    {
        lock (_streamingLock)
        {
            _streamingText.Clear();
        }

        IsBusy = true;
        SetStatusIfStarted(ServiceStatus.Running, statusMessage);
        var started = _services.TimeProvider.GetUtcNow();
        LastActivityTime = started;
        return started;
    }

    /// <summary>Ends a turn; <paramref name="isIdle"/> reports the idle status once the agent is no longer busy.</summary>
    private void EndTurn(bool isIdle)
    {
        lock (_streamingLock)
        {
            _streamingText.Clear();
        }

        // Cleared before the idle status, so a configuration change that skipped SetIdle while busy is picked up.
        IsBusy = false;
        if (isIdle)
        {
            SetIdle();
        }

        RaiseTimelineChanged();
    }

    private void OnPauseLifted()
    {
        lock (_stateLock)
        {
            // A running turn reports the status itself when it ends.
            if (!IsBusy)
            {
                SetIdle();
            }
        }
    }

    /// <summary>Reports the idle status of a started agent; a stopped or starting agent keeps its status.</summary>
    private void SetIdle()
    {
        lock (_stateLock)
        {
            if (_scheduler is not { } scheduler)
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

    /// <summary>
    /// Enters the turn gate for a chat turn, showing a waiting message while a background run holds it. Returns
    /// false, with the waiting message removed again, when <paramref name="cancellationToken"/> is cancelled first.
    /// </summary>
    private async Task<bool> TryEnterGateForChatAsync(CancellationToken cancellationToken)
    {
        if (await _turnGate.WaitAsync(0, CancellationToken.None))
        {
            return true;
        }

        string? replacedStatusMessage = null;
        var showsWaitingMessage = false;
        lock (_stateLock)
        {
            if (_scheduler is not null)
            {
                replacedStatusMessage = StatusMessage;
                StatusMessage = WaitingStatusMessage;
                showsWaitingMessage = true;
            }
        }

        try
        {
            await _turnGate.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                // Only undone while still shown: the background run may have replaced it meanwhile.
                if (showsWaitingMessage && _scheduler is not null && StatusMessage == WaitingStatusMessage)
                {
                    StatusMessage = replacedStatusMessage;
                }
            }

            return false;
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

    private async Task HandleFailureAsync(AgentTurnException exception, AgentTimelineEntry? errorEntry = null)
    {
        _logger.LogWarning("The turn of agent {Agent} failed: {Message}", Title, exception.Message);
        lock (_stateLock)
        {
            _scheduler?.ReportFailure(exception);
        }

        // Recorded before the status changes, so a reader that sees the failure status also sees the entry.
        await (errorEntry is null ? AppendErrorAsync(exception.Message) : AppendAsync(errorEntry));
        SetStatusIfStarted(
            exception.Kind is AgentFailureKind.Configuration or AgentFailureKind.UsageLimit
                ? ServiceStatus.Unavailable
                : ServiceStatus.Error,
            exception.Message);
    }

    private Task AppendErrorAsync(string text) => AppendAsync(new AgentTimelineEntry
    {
        Kind = AgentTimelineEntryKind.Error,
        Timestamp = _services.TimeProvider.GetUtcNow(),
        Text = text
    });

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

// LastActivityTime is missing in files written before it existed and then reads as null.
internal sealed record AgentStats(int BackgroundRunCount, DateTimeOffset? LastActivityTime = null);

/// <summary>The input of the browse tool for the state of a watch path.</summary>
internal sealed record AgentBrowseInput(string Path, int Depth, bool IncludeProperties, string Format);

/// <summary>The input of the get_property tool for the state of a watch path.</summary>
internal sealed record AgentPathInput(string Path);
