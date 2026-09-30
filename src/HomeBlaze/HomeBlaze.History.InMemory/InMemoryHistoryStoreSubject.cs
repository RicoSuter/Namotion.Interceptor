using System.Collections.Immutable;
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.InMemory;

/// <summary>
/// Priority-100 in-memory history store. A <see cref="ChangeQueueBackgroundService"/> [InterceptorSubject]
/// that records recordable [State] scalar property changes into per-path ring buffers and answers
/// raw and bucketed history queries through <see cref="IHistoryStore"/>. Storage concerns are delegated
/// to the graph-free <see cref="InMemoryHistoryStore"/> engine; this subject owns the change-queue glue,
/// path resolution and move detection.
/// </summary>
[Category("History")]
[Description("Records recent [State] history in memory (priority 100).")]
[InterceptorSubject]
public partial class InMemoryHistoryStoreSubject :
    ChangeQueueBackgroundService, IConfigurable, ITitleProvider, IHistoryStore, ILifecycleHandler
{
    private readonly ILogger<InMemoryHistoryStoreSubject> _logger;

    private readonly Lock _settingsLock = new();

    private HistoryChangeRecorder? _recorder;
    private InMemoryHistoryStore? _engine;
    private Settings? _settings;

    // The instant from which the subscription holds every change no session has consumed yet, so the next
    // session's coverage may start there. Only the service's sequential session flow touches these.
    private PropertyChangeQueueSubscription? _subscription;
    private DateTimeOffset _coverageStartedAt;

    public InMemoryHistoryStoreSubject(ILogger<InMemoryHistoryStoreSubject> logger)
        : base(logger)
    {
        _logger = logger;

        Priority = 100;
        MaxAgeSeconds = 60;
        MaxPointsPerProperty = 1000;
        BufferTimeMilliseconds = 250;
        MaxJsonSize = 8192;
        IsEnabled = true;

        Status = "Stopped";
    }

    /// <inheritdoc />
    public string? Title => "In-Memory History";

    // Configuration properties (persisted to JSON)

    /// <summary>
    /// Store priority. Higher values are preferred for overlapping ranges (in-memory is the highest tier).
    /// </summary>
    [Configuration]
    public partial int Priority { get; set; }

    /// <summary>
    /// Retention window in seconds. Samples older than this are evicted on sweep.
    /// </summary>
    [Configuration]
    public partial int MaxAgeSeconds { get; set; }

    /// <summary>
    /// Maximum samples retained per property path (ring-buffer capacity).
    /// </summary>
    [Configuration]
    public partial int MaxPointsPerProperty { get; set; }

    /// <summary>
    /// Change-queue buffer time in milliseconds before a batch is flushed to the recorder.
    /// </summary>
    [Configuration]
    public partial int BufferTimeMilliseconds { get; set; }

    /// <summary>
    /// Maximum JSON value size in characters; larger string values are recorded as an oversize placeholder.
    /// </summary>
    [Configuration]
    public partial int MaxJsonSize { get; set; }

    /// <summary>
    /// Whether the store records; applying a change takes effect immediately.
    /// </summary>
    [Configuration]
    public partial bool IsEnabled { get; set; }

    // State properties (runtime only)

    /// <summary>
    /// Current store status.
    /// </summary>
    [State]
    public partial string Status { get; set; }

    /// <summary>
    /// Total number of samples recorded since start.
    /// </summary>
    [State]
    public partial long RecordedCount { get; set; }

    /// <summary>
    /// Number of oversize string values replaced with a placeholder.
    /// </summary>
    [State]
    public partial long OversizeCount { get; set; }

    /// <summary>
    /// Number of samples evicted by age or capacity since start.
    /// </summary>
    [State]
    public partial long EvictedCount { get; set; }

    /// <summary>
    /// Number of distinct property paths currently tracked.
    /// </summary>
    [State]
    public partial int TrackedPropertyCount { get; set; }

    /// <summary>
    /// Total number of samples currently retained across all property paths.
    /// </summary>
    [State]
    public partial long TotalSampleCount { get; set; }

    /// <summary>
    /// Rough estimate of memory used by the retained samples in bytes.
    /// </summary>
    [State(Unit = StateUnit.Byte)]
    public partial long EstimatedMemorySize { get; set; }

    /// <summary>
    /// Average incoming changes per second (eligible [State] changes observed).
    /// </summary>
    [State]
    public partial double IncomingChangesPerSecond { get; set; }

    /// <summary>
    /// Average recorded changes per second (samples written to the engine).
    /// </summary>
    [State]
    public partial double RecordedChangesPerSecond { get; set; }

    // IHistoryStore

    /// <inheritdoc />
    public ImmutableArray<HistoryCoverage> CoverageRanges =>
        Volatile.Read(ref _engine)?.CoverageRanges ?? ImmutableArray<HistoryCoverage>.Empty;

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedAggregations => InMemoryHistoryStore.AllAggregations;

    /// <inheritdoc />
    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var engine = Volatile.Read(ref _engine);
        if (engine is null)
        {
            return Task.FromResult(
                new HistorySeries(
                    query.PropertyPath,
                    ImmutableArray<HistoryPoint>.Empty,
                    false,
                    ImmutableArray<HistoryCoverage>.Empty));
        }

        return engine.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<HistoryPoint?>(Volatile.Read(ref _engine)?.GetSampleAtOrBefore(propertyPath, asOf));
    }

    // ChangeQueueBackgroundService

    /// <inheritdoc />
    protected override IInterceptorSubjectContext Context => ((IInterceptorSubject)this).Context;

    /// <inheritdoc />
    protected override ChangeQueueProcessor CreateProcessor(PropertyChangeQueueSubscription subscription)
    {
        Settings settings;
        lock (_settingsLock)
        {
            settings = ReadSettings();
            _settings = settings;
        }

        // A recorder is not a sink that can fall behind the model, so the settled condition never holds
        // for it. Under the other rule a source-applied value does not retire an older commit, which is
        // what keeps both points in the series.
        var processor = new ChangeQueueProcessor(
            this,
            subscription,
            HistoryChangeRecorder.IsEligible,
            // Runs after ProcessAsync sets _recorder, but may outlive a session that ends by clearing it.
            (changes, _) => _recorder?.RecordBatch(changes) ?? default,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            bufferTime: TimeSpan.FromMilliseconds(settings.BufferTimeMilliseconds),
            maxQueueDepth: null,
            logger: _logger);

        // A new subscription captures from now on; a kept one still holds everything since the previous
        // session stopped consuming it, which is where the start was last set.
        if (!ReferenceEquals(subscription, _subscription))
        {
            _subscription = subscription;
            _coverageStartedAt = DateTimeOffset.UtcNow;
        }

        return processor;
    }

    /// <inheritdoc />
    protected override async Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken)
    {
        // Written by CreateProcessor on the flow that runs this, so no lock is needed.
        var settings = _settings!;
        if (!settings.IsEnabled)
        {
            StopServing();
            Status = "Disabled";
            return;
        }

        var context = ((IInterceptorSubject)this).Context;

        var resolver = context.TryGetService<ISubjectPathResolver>();
        if (resolver is null)
        {
            StopServing();
            Status = "Error";
            _logger.LogError("No ISubjectPathResolver is registered in the context; cannot record history.");
            return;
        }

        var engine = new InMemoryHistoryStore(
            priority: Priority,
            maxPointsPerProperty: settings.MaxPointsPerProperty,
            maxAge: TimeSpan.FromSeconds(settings.MaxAgeSeconds),
            maxJsonSize: settings.MaxJsonSize,
            getUtcNow: () => DateTimeOffset.UtcNow);

        // The subscription precedes the coverage session, so no change can fall inside claimed coverage
        // without reaching the engine.
        var recorder = new HistoryChangeRecorder(engine, resolver);
        _recorder = recorder;

        engine.BeginCoverageSession(_coverageStartedAt);
        Volatile.Write(ref _engine, engine);

        Status = "Running";

        // The sweep loop ends with the session, so a processing fault surfaces to the service's retry
        // instead of waiting behind a loop that only a stop or restart would end.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sweepTask = RunSweepLoopAsync(engine, session.Token);
        var faulted = false;
        try
        {
            await processor.ProcessAsync(session.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            faulted = true;
            throw;
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await sweepTask.ConfigureAwait(false);

            // The processor has stopped consuming, so every later change waits in the subscription for the
            // next session, whose coverage starts where this one ends. The engine stays queryable, but it is
            // no longer recording, so its coverage must end here rather than following the clock forever.
            _coverageStartedAt = DateTimeOffset.UtcNow;
            engine.EndCoverageSession(_coverageStartedAt);
            Status = faulted ? "Error" : "Stopped";
        }
    }

    private async Task RunSweepLoopAsync(InMemoryHistoryStore engine, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    engine.Sweep();
                }
                catch (Exception exception)
                {
                    // The samples stay in memory, so the next tick retries; ending the session instead would
                    // discard them.
                    _logger.LogError(exception, "Periodic history sweep failed; will retry on the next tick.");
                }

                RefreshMetrics(engine);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            RefreshMetrics(engine);
        }
    }

    // A session that does not record serves nothing, like a store that started in that state; otherwise
    // the previous session's samples would stay queryable after a restart into it.
    private void StopServing()
    {
        Volatile.Write(ref _engine, null);
        _recorder = null;

        RecordedCount = 0;
        OversizeCount = 0;
        EvictedCount = 0;
        TrackedPropertyCount = 0;
        TotalSampleCount = 0;
        EstimatedMemorySize = 0;
        IncomingChangesPerSecond = 0;
        RecordedChangesPerSecond = 0;
    }

    private void RefreshMetrics(InMemoryHistoryStore engine)
    {
        RecordedCount = engine.RecordedCount;
        OversizeCount = engine.OversizeCount;
        EvictedCount = engine.EvictedCount;
        TrackedPropertyCount = engine.TrackedPropertyCount;
        TotalSampleCount = engine.TotalSampleCount;
        EstimatedMemorySize = engine.EstimatedMemoryBytes;
        IncomingChangesPerSecond = _recorder?.IncomingChangesPerSecond ?? 0;
        RecordedChangesPerSecond = _recorder?.RecordedChangesPerSecond ?? 0;
    }

    // ILifecycleHandler

    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextDetach)
        {
            _recorder?.Forget(change.Subject);
        }
    }

    // IConfigurable

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // A restart discards the samples held in memory, so only a changed start-time setting restarts. Under
        // the lock CreateProcessor holds while it reads and publishes, so an apply racing a restart either sees
        // the settings that restart read or is read by it; outside it, a revert could compare against the
        // stale settings and be lost.
        lock (_settingsLock)
        {
            if (_settings is { } started && started != ReadSettings())
            {
                RequestRestart();
            }
        }

        return Task.CompletedTask;
    }

    private Settings ReadSettings() =>
        new(IsEnabled, MaxAgeSeconds, MaxPointsPerProperty, MaxJsonSize, BufferTimeMilliseconds);

    // Settings read once per start. Priority is not one: HistoryStoreMerger reads it live from this subject.
    private sealed record Settings(
        bool IsEnabled, int MaxAgeSeconds, int MaxPointsPerProperty, int MaxJsonSize, int BufferTimeMilliseconds);
}
