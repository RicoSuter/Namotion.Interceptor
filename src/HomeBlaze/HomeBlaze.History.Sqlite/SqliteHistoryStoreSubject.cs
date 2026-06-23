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

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Priority-50 SQLite history store. A <see cref="BackgroundService"/> [InterceptorSubject]
/// that records recordable [State] scalar property changes into partitioned SQLite database files and
/// answers raw and bucketed history queries through <see cref="IHistoryStore"/>. Storage concerns are
/// delegated to the graph-free <see cref="SqliteHistoryStore"/> engine; this subject owns the change-queue
/// glue, path resolution, move detection, the periodic flush plus sweep loop, and the shutdown flush.
/// </summary>
[Category("History")]
[Description("Persists [State] history to partitioned SQLite files (priority 50).")]
[InterceptorSubject]
public partial class SqliteHistoryStoreSubject : BackgroundService, IConfigurable, IHistoryStore, ILifecycleHandler
{
    private readonly ILogger<SqliteHistoryStoreSubject> _logger;

    private readonly ThroughputCounter _incomingThroughput = new();
    private readonly ThroughputCounter _recordedThroughput = new();

    // Last canonical subject path seen per subject, used for move detection. The resolver's own
    // cache is cleared on every structural change, so GetPath() is always current; comparing the
    // returned path to the stored one detects a move without depending on lifecycle event delivery.
    private readonly Dictionary<IInterceptorSubject, string> _lastSubjectPath = new();
    private readonly object _pathCacheLock = new();

    private SqliteHistoryStore? _engine;

    public SqliteHistoryStoreSubject(ILogger<SqliteHistoryStoreSubject> logger)
    {
        _logger = logger;

        Priority = 50;
        MaxAgeDays = 365;
        FlushIntervalSeconds = 10;
        BufferTimeMilliseconds = 250;
        PartitionInterval = PartitionInterval.Weekly;
        DatabasePath = string.Empty;
        MaxJsonSize = 8192;
        IsEnabled = true;

        Status = "Stopped";
    }

    // Configuration properties (persisted to JSON)

    /// <summary>
    /// Store priority. Higher values are preferred for overlapping ranges (SQLite is the persistent tier).
    /// </summary>
    [Configuration]
    public partial int Priority { get; set; }

    /// <summary>
    /// Retention window in days. Partition files whose range ends before now minus this are deleted on sweep.
    /// </summary>
    [Configuration]
    public partial int MaxAgeDays { get; set; }

    /// <summary>
    /// Interval in seconds between flushes of the pending sample batch to the partition files.
    /// </summary>
    [Configuration]
    public partial int FlushIntervalSeconds { get; set; }

    /// <summary>
    /// Change-queue buffer time in milliseconds before a batch is flushed to the recorder.
    /// </summary>
    [Configuration]
    public partial int BufferTimeMilliseconds { get; set; }

    /// <summary>
    /// The time span a single partition database file covers.
    /// </summary>
    [Configuration]
    public partial PartitionInterval PartitionInterval { get; set; }

    /// <summary>
    /// Directory that holds the partition database files. Required; the store reports an error if empty.
    /// </summary>
    [Configuration]
    public partial string DatabasePath { get; set; }

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
    /// Number of samples currently queued (not yet flushed to the partition files).
    /// </summary>
    [State]
    public partial int QueueDepth { get; set; }

    /// <summary>
    /// Cumulative number of samples dropped (kept for symmetry; stays zero because the change queue is unbounded).
    /// </summary>
    [State]
    public partial long DropCount { get; set; }

    /// <summary>
    /// Timestamp of the last successful flush, or null before the first flush.
    /// </summary>
    [State]
    public partial DateTimeOffset? LastFlushUtc { get; set; }

    /// <summary>
    /// Message of the last error encountered during flush or sweep, or null when healthy.
    /// </summary>
    [State]
    public partial string? LastError { get; set; }

    /// <summary>
    /// Estimated on-disk storage used by the partition and moves database files in bytes.
    /// </summary>
    [State]
    public partial long EstimatedStorageBytes { get; set; }

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
    public IReadOnlySet<string> SupportedAggregations => SqliteHistoryStore.AllAggregations;

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

    /// <summary>
    /// Test-only hook that forces the engine to flush its pending samples immediately, so queued
    /// changes become queryable without waiting for the interval flush. Returns a completed task
    /// (no-op) before the engine is built.
    /// </summary>
    internal Task FlushNowAsync(CancellationToken cancellationToken = default)
    {
        return _engine?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
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

        if (string.IsNullOrEmpty(DatabasePath))
        {
            Status = "Error";
            _logger.LogError("No DatabasePath is configured; cannot persist history to SQLite files.");
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

        var engine = new SqliteHistoryStore(
            priority: Priority,
            databaseDirectory: DatabasePath,
            partitionInterval: PartitionInterval,
            maxAge: TimeSpan.FromDays(MaxAgeDays),
            maxJsonSize: MaxJsonSize,
            getUtcNow: () => DateTimeOffset.UtcNow);
        _engine = engine;

        // Drop detached subjects from the move-detection cache (memory hygiene).
        context.AddService(this);

        // Construct the processor first so its change-queue subscription is live before the first await
        // (BackgroundService.StartAsync returns at that point); this minimizes the startup gap during
        // which changes would otherwise go unobserved. The engine buffers samples and flushes on an interval,
        // so the queue is never stuck and needs no bound (maxQueueDepth: null).
        using var processor = new ChangeQueueProcessor(
            this,
            context,
            propertyReference => propertyReference.TryGetRegisteredProperty() is { } registered && registered.HasHistory(),
            (changes, _) => RecordBatch(engine, resolver, changes),
            TimeSpan.FromMilliseconds(BufferTimeMilliseconds),
            maxQueueDepth: null,
            logger: _logger);

        Status = "Running";

        var flushTask = RunFlushLoopAsync(engine, stoppingToken);
        try
        {
            await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await flushTask.ConfigureAwait(false);

            // Final shutdown flush so the un-flushed tail is persisted. The stopping token is already
            // cancelled here, so a fresh bounded token gives the flush a chance to complete (the engine
            // keeps pending samples on failure, so a timeout simply leaves them for the next start).
            using var shutdownCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await engine.FlushAsync(shutdownCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Final history flush on shutdown failed; pending samples were not persisted.");
            }

            RefreshMetrics(engine);
            engine.Dispose();
            Status = "Stopped";
        }
    }

    private ValueTask RecordBatch(
        SqliteHistoryStore engine, ISubjectPathResolver resolver, ReadOnlyMemory<SubjectPropertyChange> changes)
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

    private async Task RunFlushLoopAsync(SqliteHistoryStore engine, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await engine.FlushAsync(stoppingToken).ConfigureAwait(false);
                    engine.Sweep();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    // The engine keeps pending samples on a flush failure, so the next tick retries; the
                    // loop must not crash and stop recording.
                    _logger.LogError(exception, "Periodic history flush or sweep failed; will retry on the next tick.");
                }

                RefreshMetrics(engine);

                await Task.Delay(TimeSpan.FromSeconds(FlushIntervalSeconds), stoppingToken).ConfigureAwait(false);
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

    private void RefreshMetrics(SqliteHistoryStore engine)
    {
        RecordedCount = engine.RecordedCount;
        OversizeCount = engine.OversizeCount;
        QueueDepth = engine.QueueDepth;
        EstimatedStorageBytes = engine.EstimatedStorageBytes;
        LastFlushUtc = engine.LastFlushUtc;
        LastError = engine.LastError;
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
        // Size knobs (MaxAgeDays, MaxJsonSize, PartitionInterval, DatabasePath) and BufferTime and
        // FlushInterval are read once when the engine and change-queue processor are built in ExecuteAsync.
        // Like OpcUaServer, configuration changes take effect on the next start; the host restarts the
        // background service to apply them.
        return Task.CompletedTask;
    }
}
