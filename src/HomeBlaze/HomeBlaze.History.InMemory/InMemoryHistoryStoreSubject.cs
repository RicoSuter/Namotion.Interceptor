using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.InMemory;

/// <summary>
/// Priority-100 in-memory history store. A <see cref="BackgroundService"/> [InterceptorSubject]
/// that records recordable [State] scalar property changes into per-path ring buffers and answers
/// raw and bucketed history queries through <see cref="IHistoryStore"/>. Storage concerns are delegated
/// to the graph-free <see cref="InMemoryHistoryStore"/> engine; this subject owns the change-queue glue,
/// path resolution and move detection.
/// </summary>
[Category("History")]
[Description("Records recent [State] history in memory (priority 100).")]
[InterceptorSubject]
public partial class InMemoryHistoryStoreSubject : BackgroundService, IConfigurable, IHistoryStore, ILifecycleHandler
{
    private readonly ILogger<InMemoryHistoryStoreSubject> _logger;

    private readonly ThroughputCounter _incomingThroughput = new();
    private readonly ThroughputCounter _recordedThroughput = new();

    // Last canonical subject path seen per subject, used for move detection. The resolver's own
    // cache is cleared on every structural change, so GetPath() is always current; comparing the
    // returned path to the stored one detects a move without depending on lifecycle event delivery.
    private readonly Dictionary<IInterceptorSubject, string> _lastSubjectPath = new();
    private readonly Lock _pathCacheLock = new();

    private InMemoryHistoryStore? _engine;

    public InMemoryHistoryStoreSubject(ILogger<InMemoryHistoryStoreSubject> logger)
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
    /// Whether the store is enabled and should auto-start on application startup.
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
    /// Cumulative number of samples evicted by age or capacity.
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
    [State]
    public partial long EstimatedMemoryBytes { get; set; }

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
    public HistoryCoverage CurrentCoverage =>
        _engine?.CurrentCoverage ?? new HistoryCoverage(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedAggregations => InMemoryHistoryStore.AllAggregations;

    /// <inheritdoc />
    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        if (_engine is null)
        {
            return Task.FromResult(
                new HistorySeries(query.PropertyPath, System.Collections.Immutable.ImmutableArray<HistoryPoint>.Empty, false));
        }

        return _engine.QueryAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        return new ValueTask<HistoryPoint?>(_engine?.GetSampleAtOrBefore(propertyPath, asOf));
    }

    // BackgroundService

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            Status = "Disabled";
            return;
        }

        var context = ((IInterceptorSubject)this).Context;

        var resolver = context.TryGetService<ISubjectPathResolver>();
        if (resolver is null)
        {
            Status = "Error";
            _logger.LogError("No ISubjectPathResolver is registered in the context; cannot record history.");
            return;
        }

        var engine = new InMemoryHistoryStore(
            priority: Priority,
            maxPointsPerProperty: MaxPointsPerProperty,
            maxAge: TimeSpan.FromSeconds(MaxAgeSeconds),
            maxJsonSize: MaxJsonSize,
            getUtcNow: () => DateTimeOffset.UtcNow);
        _engine = engine;

        // Drop detached subjects from the move-detection cache (memory hygiene).
        context.AddService(this);

        // Construct the processor first so its change-queue subscription is live before the first await
        // (BackgroundService.StartAsync returns at that point); this minimizes the startup gap during
        // which changes would otherwise go unobserved. InMemory is a direct recorder, so the buffered
        // queue is never stuck and needs no bound (maxQueueDepth: null).
        using var processor = new ChangeQueueProcessor(
            this,
            context,
            propertyReference => propertyReference.TryGetRegisteredProperty() is { } registered && registered.HasHistory(),
            (changes, _) => RecordBatch(engine, resolver, changes),
            TimeSpan.FromMilliseconds(BufferTimeMilliseconds),
            maxQueueDepth: null,
            logger: _logger);

        Status = "Running";

        var sweepTask = RunSweepLoopAsync(engine, stoppingToken);
        try
        {
            await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await sweepTask.ConfigureAwait(false);
            Status = "Stopped";
        }
    }

    private ValueTask RecordBatch(
        InMemoryHistoryStore engine, ISubjectPathResolver resolver, ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        for (var index = 0; index < span.Length; index++)
        {
            var change = span[index];

            var registered = change.Property.TryGetRegisteredProperty();
            if (registered is null || !registered.HasHistory())
            {
                continue;
            }

            _incomingThroughput.Add(1);

            var subject = change.Property.Subject;
            var subjectPath = resolver.GetPath(subject, PathStyle.Canonical);
            if (subjectPath is null)
            {
                // Subject is no longer reachable (detached between change and flush); skip.
                continue;
            }

            var propertyName = change.Property.Name;
            var fullPath = JoinPath(subjectPath, propertyName);

            lock (_pathCacheLock)
            {
                if (_lastSubjectPath.TryGetValue(subject, out var previousSubjectPath) &&
                    !string.Equals(previousSubjectPath, subjectPath, StringComparison.Ordinal))
                {
                    engine.RecordMove(
                        change.ChangedTimestamp,
                        JoinPath(previousSubjectPath, propertyName),
                        fullPath);
                }

                _lastSubjectPath[subject] = subjectPath;
            }

            engine.Record(fullPath, change.ChangedTimestamp, change.GetNewValue<object>(), registered.Type);
            _recordedThroughput.Add(1);
        }

        return ValueTask.CompletedTask;
    }

    private async Task RunSweepLoopAsync(InMemoryHistoryStore engine, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                engine.Sweep();
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

    private void RefreshMetrics(InMemoryHistoryStore engine)
    {
        RecordedCount = engine.RecordedCount;
        OversizeCount = engine.OversizeCount;
        EvictedCount = engine.EvictedCount;
        TrackedPropertyCount = engine.TrackedPropertyCount;
        TotalSampleCount = engine.TotalSampleCount;
        EstimatedMemoryBytes = engine.EstimatedMemoryBytes;
        IncomingChangesPerSecond = _incomingThroughput.CurrentRate;
        RecordedChangesPerSecond = _recordedThroughput.CurrentRate;
    }

    // Joins a canonical subject path with a property name. The root subject path is "/", so a root
    // property is "/Temperature" (not "//Temperature"); a child at "/Child" yields "/Child/Pressure".
    private static string JoinPath(string subjectPath, string propertyName) =>
        subjectPath == "/" ? "/" + propertyName : subjectPath + "/" + propertyName;

    // ILifecycleHandler

    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextDetach)
        {
            lock (_pathCacheLock)
            {
                _lastSubjectPath.Remove(change.Subject);
            }
        }
    }

    // IConfigurable

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // Size knobs (MaxPointsPerProperty, MaxAgeSeconds, MaxJsonSize) and BufferTime are read once when
        // the engine and change-queue processor are built in ExecuteAsync. Like OpcUaServer, configuration
        // changes take effect on the next start; the host restarts the background service to apply them.
        return Task.CompletedTask;
    }
}
