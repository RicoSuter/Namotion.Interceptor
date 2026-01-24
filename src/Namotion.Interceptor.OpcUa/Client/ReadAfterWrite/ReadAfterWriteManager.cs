using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Resilience;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;

/// <summary>
/// Manages read-after-writes for properties where the server revised SamplingInterval=0 to non-zero.
/// Maintains a NodeId-to-property index for O(1) lookups and handles automatic cleanup.
/// Thread-safe. All state is protected by a single lock for simplicity.
/// </summary>
internal sealed class ReadAfterWriteManager : IAsyncDisposable
{
    private readonly Func<ISession?> _sessionProvider;
    private readonly ISubjectSource _source;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly CancellationTokenSource _cts = new();

    // NodeId -> Property (for O(1) lookup during reads)
    private readonly Dictionary<NodeId, RegisteredSubjectProperty> _propertyIndex = new();

    // NodeId -> RevisedInterval (only properties that need read-after-writes)
    private readonly Dictionary<NodeId, TimeSpan> _revisedIntervals = new();

    // NodeId -> ReadAt time (pending scheduled reads)
    private readonly Dictionary<NodeId, DateTime> _pendingReads = new();

    private DateTime _earliestReadTime = DateTime.MaxValue;
    private ISession? _lastKnownSession;
    private int _disposed;

    /// <summary>
    /// Gets metrics for read-after-write operations.
    /// </summary>
    public ReadAfterWriteMetrics Metrics { get; } = new();

    /// <summary>
    /// Gets the number of registered properties.
    /// </summary>
    public int RegisteredPropertyCount
    {
        get { lock (_lock) { return _propertyIndex.Count; } }
    }

    /// <summary>
    /// Gets the number of pending read-after-writes.
    /// </summary>
    public int PendingReadCount
    {
        get { lock (_lock) { return _pendingReads.Count; } }
    }

    /// <summary>
    /// Creates a new read-after-write manager.
    /// </summary>
    /// <param name="sessionProvider">Function to get current session.</param>
    /// <param name="source">The subject source for applying read values.</param>
    /// <param name="configuration">OPC UA client configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public ReadAfterWriteManager(
        Func<ISession?> sessionProvider,
        ISubjectSource source,
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        _sessionProvider = sessionProvider;
        _source = source;
        _configuration = configuration;
        _logger = logger;
        _circuitBreaker = new CircuitBreaker(
            configuration.PollingCircuitBreakerThreshold,
            configuration.PollingCircuitBreakerCooldown);
        _timer = new Timer(OnTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Registers a property with its NodeId. Call when a property is set up with OPC UA monitoring.
    /// </summary>
    /// <param name="nodeId">The OPC UA node ID.</param>
    /// <param name="property">The property.</param>
    /// <param name="requestedSamplingInterval">The requested sampling interval (0 = exception-based).</param>
    /// <param name="revisedSamplingInterval">The server's revised sampling interval.</param>
    public void RegisterProperty(
        NodeId nodeId,
        RegisteredSubjectProperty property,
        int? requestedSamplingInterval,
        TimeSpan revisedSamplingInterval)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        lock (_lock)
        {
            _propertyIndex[nodeId] = property;

            // Track for read-after-writes if: requested 0 (exception-based) but server revised to > 0
            if (requestedSamplingInterval == 0 && revisedSamplingInterval > TimeSpan.Zero)
            {
                _revisedIntervals[nodeId] = revisedSamplingInterval;

                _logger.LogDebug(
                    "Property {PropertyName} registered for read-after-writes: " +
                    "requested SamplingInterval=0, revised to {RevisedInterval}ms.",
                    property?.Name ?? nodeId.ToString(), revisedSamplingInterval.TotalMilliseconds);
            }
        }
    }

    /// <summary>
    /// Unregisters a property. Call when a property is released or subject detaches.
    /// Removes from index and cancels any pending reads.
    /// </summary>
    /// <param name="nodeId">The OPC UA node ID.</param>
    public void UnregisterProperty(NodeId nodeId)
    {
        lock (_lock)
        {
            _propertyIndex.Remove(nodeId);
            _revisedIntervals.Remove(nodeId);

            if (_pendingReads.Remove(nodeId))
            {
                // Recalculate earliest if we removed a pending read
                RecalculateEarliestLocked();
            }
        }
    }

    /// <summary>
    /// Notifies that a property was successfully written. Schedules a read-after-write if needed.
    /// </summary>
    /// <param name="nodeId">The OPC UA node ID that was written.</param>
    public void OnPropertyWritten(NodeId nodeId)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            // Check for session change
            var currentSession = _sessionProvider();
            if (!ReferenceEquals(_lastKnownSession, currentSession))
            {
                ClearPendingReadsLocked();
                _lastKnownSession = currentSession;
                _circuitBreaker.Reset();

                _logger.LogDebug("Session changed. Cleared pending read-after-writes.");
            }

            // Only schedule if this property needs read-after-writes
            if (!_revisedIntervals.TryGetValue(nodeId, out var revisedInterval))
            {
                return;
            }

            var readAt = DateTime.UtcNow + revisedInterval + _configuration.ReadAfterWriteBuffer;

            if (_pendingReads.ContainsKey(nodeId))
            {
                Metrics.RecordCoalesced();
            }
            else
            {
                Metrics.RecordScheduled();
            }

            _pendingReads[nodeId] = readAt;

            // Only reschedule timer if this is earlier than current earliest
            if (readAt < _earliestReadTime)
            {
                _earliestReadTime = readAt;
                RescheduleTimerLocked();
            }
        }
    }

    /// <summary>
    /// Clears all pending reads. Call on session change or reconnection.
    /// Does NOT clear registered properties or revised intervals - those remain valid.
    /// </summary>
    public void ClearPendingReads()
    {
        lock (_lock)
        {
            ClearPendingReadsLocked();
        }
    }

    /// <summary>
    /// Clears all state including registered properties. Call on full reconnection.
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _propertyIndex.Clear();
            _revisedIntervals.Clear();
            ClearPendingReadsLocked();
            _lastKnownSession = null;
        }
    }

    /// <summary>
    /// Clears pending reads and stops the timer. Must be called while holding _lock.
    /// </summary>
    private void ClearPendingReadsLocked()
    {
        _pendingReads.Clear();
        _earliestReadTime = DateTime.MaxValue;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Tries to get a property by its NodeId. O(1) lookup.
    /// </summary>
    public bool TryGetProperty(NodeId nodeId, out RegisteredSubjectProperty? property)
    {
        lock (_lock)
        {
            if (_propertyIndex.TryGetValue(nodeId, out var prop))
            {
                property = prop;
                return true;
            }
            property = null;
            return false;
        }
    }

    private void OnTimerCallback(object? state)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _ = ProcessDueReadsAsync();
    }

    private async Task ProcessDueReadsAsync()
    {
        if (!_circuitBreaker.ShouldAttempt())
        {
            _logger.LogDebug("Read-after-write circuit breaker open, skipping.");
            RescheduleTimer();
            return;
        }

        List<(NodeId NodeId, RegisteredSubjectProperty Property)> dueReads;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            dueReads = new List<(NodeId, RegisteredSubjectProperty)>();

            foreach (var kvp in _pendingReads)
            {
                if (kvp.Value <= now && _propertyIndex.TryGetValue(kvp.Key, out var property))
                {
                    dueReads.Add((kvp.Key, property));
                }
            }

            foreach (var (nodeId, _) in dueReads)
            {
                _pendingReads.Remove(nodeId);
            }

            RecalculateEarliestLocked();
        }

        if (dueReads.Count == 0)
        {
            RescheduleTimer();
            return;
        }

        var session = _sessionProvider();
        if (session is null || !session.Connected)
        {
            _logger.LogDebug("Skipping read-after-writes - session not connected.");
            RescheduleTimer();
            return;
        }

        try
        {
            var readValues = new ReadValueIdCollection(dueReads.Count);
            foreach (var (nodeId, _) in dueReads)
            {
                readValues.Add(new ReadValueId
                {
                    NodeId = nodeId,
                    AttributeId = Opc.Ua.Attributes.Value
                });
            }

            var response = await session.ReadAsync(
                requestHeader: null,
                maxAge: 0,
                timestampsToReturn: TimestampsToReturn.Source,
                readValues,
                _cts.Token).ConfigureAwait(false);

            var successCount = 0;
            var receivedTimestamp = DateTimeOffset.UtcNow;

            for (var i = 0; i < response.Results.Count && i < dueReads.Count; i++)
            {
                var result = response.Results[i];
                if (!StatusCode.IsGood(result.StatusCode))
                {
                    continue;
                }

                var (_, property) = dueReads[i];
                var value = _configuration.ValueConverter.ConvertToPropertyValue(result.Value, property);

                property.SetValueFromSource(_source, result.SourceTimestamp, receivedTimestamp, value);
                successCount++;
            }

            Metrics.RecordExecuted(successCount);
            _circuitBreaker.RecordSuccess();

            _logger.LogDebug(
                "Completed {SuccessCount}/{TotalCount} read-after-writes.",
                successCount, dueReads.Count);
        }
        catch (Exception ex)
        {
            Metrics.RecordFailed();
            if (_circuitBreaker.RecordFailure())
            {
                _logger.LogError(ex, "Read-after-write circuit breaker opened after failures.");
            }
            else
            {
                _logger.LogWarning(ex, "Failed to execute read-after-writes.");
            }
        }
        finally
        {
            RescheduleTimer();
        }
    }

    private void RescheduleTimer()
    {
        lock (_lock)
        {
            RescheduleTimerLocked();
        }
    }

    private void RescheduleTimerLocked()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (_earliestReadTime == DateTime.MaxValue)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var delay = _earliestReadTime - DateTime.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void RecalculateEarliestLocked()
    {
        _earliestReadTime = _pendingReads.Count == 0
            ? DateTime.MaxValue
            : _pendingReads.Values.Min();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _logger.LogDebug("Disposing ReadAfterWriteManager. Metrics: {Metrics}", Metrics);

        await _cts.CancelAsync().ConfigureAwait(false);

        lock (_lock)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        await _timer.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();

        lock (_lock)
        {
            _propertyIndex.Clear();
            _revisedIntervals.Clear();
            _pendingReads.Clear();
            _earliestReadTime = DateTime.MaxValue;
        }
    }
}
