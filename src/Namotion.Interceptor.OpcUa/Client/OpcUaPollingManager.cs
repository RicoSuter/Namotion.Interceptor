using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Manages polling-based reading for OPC UA nodes that don't support subscriptions.
/// Provides automatic fallback when subscription creation fails.
///
/// Thread-Safety: This class is thread-safe for concurrent access to all public methods.
/// The polling loop runs on a background thread and uses concurrent collections for item management.
///
/// Initialization Order:
/// 1. Construct the OpcUaPollingManager instance
/// 2. Call Start() to begin the background polling loop (idempotent - safe to call multiple times)
/// 3. Add items via AddItem() as subscription failures are detected
/// Note: Start() can be called before or after items are added - the polling loop will process
/// items whenever the collection is non-empty.
///
/// Behavior During Disconnection: When the OPC UA session is disconnected, polling is suspended
/// and cached values become stale. Upon reconnection, cached values are reset and polling resumes,
/// causing all polled properties to receive fresh values on the next polling cycle. This is acceptable
/// for polling-based monitoring where some data loss during disconnection is expected and tolerable.
///
/// Circuit Breaker: After 5 consecutive polling failures, the circuit breaker opens and suspends
/// polling for 30 seconds. This prevents resource exhaustion when the server is persistently unavailable.
/// The circuit automatically attempts to close after the cooldown period.
/// </summary>
internal sealed class OpcUaPollingManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly OpcUaSessionManager _sessionManager;
    private readonly ISubjectUpdater _updater;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly TimeSpan _disposalTimeout;

    private readonly ConcurrentDictionary<string, PollingItem> _pollingItems = new();
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _startLock = new();

    private Task? _pollingTask;
    private ISession? _lastKnownSession;
    private int _disposed;

    // Circuit breaker state
    private const int CircuitBreakerFailureThreshold = 5;
    private const int CircuitBreakerCooldownSeconds = 30;
    private int _consecutiveFailures;
    private int _circuitOpen; // 0 = closed, 1 = open
    private DateTimeOffset _circuitOpenedAt;

    // Metrics
    private long _totalReads;
    private long _failedReads;
    private long _valueChanges;
    private long _slowPolls;
    private long _circuitBreakerTrips;

    public OpcUaPollingManager(
        ILogger logger,
        OpcUaSessionManager sessionManager,
        ISubjectUpdater updater,
        TimeSpan pollingInterval,
        int batchSize,
        TimeSpan disposalTimeout)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(updater);

        if (pollingInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollingInterval), "Polling interval must be greater than zero");

        if (pollingInterval < TimeSpan.FromMilliseconds(100))
            logger.LogWarning("Polling interval of {Interval}ms is very short and may cause excessive server load", pollingInterval.TotalMilliseconds);

        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than zero");

        if (disposalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(disposalTimeout), "Disposal timeout must be greater than zero");

        _logger = logger;
        _sessionManager = sessionManager;
        _updater = updater;
        _pollingInterval = pollingInterval;
        _batchSize = batchSize;
        _disposalTimeout = disposalTimeout;
        _timer = new PeriodicTimer(pollingInterval);
    }

    /// <summary>
    /// Gets the number of items currently being polled.
    /// </summary>
    public int PollingItemCount => _pollingItems.Count;

    /// <summary>
    /// Gets the total number of successful read operations performed.
    /// </summary>
    public long TotalReads => Interlocked.Read(ref _totalReads);

    /// <summary>
    /// Gets the total number of failed read operations.
    /// </summary>
    public long FailedReads => Interlocked.Read(ref _failedReads);

    /// <summary>
    /// Gets the total number of value changes detected and processed.
    /// </summary>
    public long ValueChanges => Interlocked.Read(ref _valueChanges);

    /// <summary>
    /// Gets the total number of slow polls (poll duration exceeded polling interval).
    /// </summary>
    public long SlowPolls => Interlocked.Read(ref _slowPolls);

    /// <summary>
    /// Gets the total number of times the circuit breaker has tripped due to persistent failures.
    /// </summary>
    public long CircuitBreakerTrips => Interlocked.Read(ref _circuitBreakerTrips);

    /// <summary>
    /// Gets whether the circuit breaker is currently open (polling suspended due to persistent failures).
    /// </summary>
    public bool IsCircuitOpen => Volatile.Read(ref _circuitOpen) == 1;

    /// <summary>
    /// Gets whether the polling manager is currently running.
    /// </summary>
    public bool IsRunning => _pollingTask != null && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Starts the polling loop in the background.
    /// Idempotent - safe to call multiple times (subsequent calls are ignored).
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if already disposed</exception>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(OpcUaPollingManager));

        lock (_startLock)
        {
            if (_pollingTask != null)
            {
                _logger.LogDebug("Polling manager already started, ignoring duplicate start request");
                return;
            }

            _pollingTask = Task.Run(async () => await PollLoopAsync(_cts.Token));
        }

        _logger.LogInformation("OPC UA polling manager started with interval {Interval}ms", _pollingInterval.TotalMilliseconds);
    }

    /// <summary>
    /// Adds a monitored item to polling fallback.
    /// Called when subscription creation fails for this item.
    /// </summary>
    public void AddItem(MonitoredItem monitoredItem)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        var key = monitoredItem.StartNodeId.ToString();

        // Extract RegisteredSubjectProperty from handle
        if (monitoredItem.Handle is not RegisteredSubjectProperty property)
        {
            _logger.LogWarning("Cannot add item {NodeId} to polling - invalid handle", key);
            return;
        }

        var pollingItem = new PollingItem(
            monitoredItem.StartNodeId,
            Property: property,
            LastValue: null
        );

        if (_pollingItems.TryAdd(key, pollingItem))
        {
            _logger.LogInformation("Added node {NodeId} to polling fallback (subscription not supported)", key);
        }
    }

    /// <summary>
    /// Removes an item from polling.
    /// </summary>
    public void RemoveItem(NodeId nodeId)
    {
        var key = nodeId.ToString();
        if (_pollingItems.TryRemove(key, out _))
        {
            _logger.LogDebug("Removed node {NodeId} from polling", key);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken))
            {
                await PollItemsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in polling loop");
        }

        _logger.LogInformation("OPC UA polling manager stopped");
    }

    private async Task PollItemsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (_pollingItems.IsEmpty)
        {
            return;
        }

        // Circuit breaker: check if we should attempt to close the circuit after cooldown
        if (Volatile.Read(ref _circuitOpen) == 1)
        {
            var timeSinceOpened = DateTimeOffset.UtcNow - _circuitOpenedAt;
            if (timeSinceOpened.TotalSeconds >= CircuitBreakerCooldownSeconds)
            {
                _logger.LogInformation("Circuit breaker cooldown period elapsed, attempting to resume polling");
                Volatile.Write(ref _circuitOpen, 0);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            else
            {
                _logger.LogDebug("Circuit breaker is open, skipping poll (cooldown: {Remaining}s remaining)",
                    (int)(CircuitBreakerCooldownSeconds - timeSinceOpened.TotalSeconds));
                return;
            }
        }

        var session = _sessionManager.CurrentSession;
        if (session == null)
        {
            _logger.LogDebug("No active session available for polling");
            Volatile.Write(ref _lastKnownSession, null);
            return;
        }

        // Detect session change (reconnection)
        var lastSession = Volatile.Read(ref _lastKnownSession);
        if (lastSession != null && !ReferenceEquals(lastSession, session))
        {
            _logger.LogInformation("Session change detected, resetting polled item values and circuit breaker");
            ResetPolledValues();
            // Reset circuit breaker on session change (fresh start)
            Volatile.Write(ref _circuitOpen, 0);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        Volatile.Write(ref _lastKnownSession, session);

        // Validate session is connected and operational
        if (!session.Connected)
        {
            _logger.LogDebug("Session not connected, skipping poll");
            return;
        }

        var startTime = DateTimeOffset.UtcNow;
        var pollSucceeded = false;

        try
        {
            // Get snapshot of items to poll - creates a copy to avoid concurrent modifications
            var itemsToRead = _pollingItems.Values.ToArray();

            // Process in batches using direct indexing
            for (int i = 0; i < itemsToRead.Length; i += _batchSize)
            {
                var batchSize = Math.Min(_batchSize, itemsToRead.Length - i);
                var batch = new ArraySegment<PollingItem>(itemsToRead, i, batchSize);
                await ReadBatchAsync(session, batch, cancellationToken);
            }

            pollSucceeded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling items");
            pollSucceeded = false;
        }
        finally
        {
            // Circuit breaker: track success/failure
            if (pollSucceeded)
            {
                // Reset consecutive failures on success
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            else
            {
                // Increment failures and check threshold
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures >= CircuitBreakerFailureThreshold)
                {
                    if (Interlocked.CompareExchange(ref _circuitOpen, 1, 0) == 0)
                    {
                        _circuitOpenedAt = DateTimeOffset.UtcNow;
                        Interlocked.Increment(ref _circuitBreakerTrips);
                        _logger.LogError("Circuit breaker opened after {Failures} consecutive failures. Polling suspended for {Cooldown}s",
                            failures, CircuitBreakerCooldownSeconds);
                    }
                }
            }

            // Detect slow polls that exceed the polling interval
            var duration = DateTimeOffset.UtcNow - startTime;
            if (duration > _pollingInterval)
            {
                Interlocked.Increment(ref _slowPolls);
                _logger.LogWarning("Slow poll detected: polling took {Duration}ms, which exceeds interval of {Interval}ms. Consider increasing polling interval or batch size.",
                    duration.TotalMilliseconds, _pollingInterval.TotalMilliseconds);
            }
        }
    }

    private void ResetPolledValues()
    {
        // Clear cached values on session change to force re-notification
        // Take snapshot to avoid TOCTOU issues with concurrent modifications
        // Note: There is a benign race condition where items removed between ToArray() and assignment
        // could be briefly re-added, but this is acceptable as they will be removed again on next cleanup.
        // This pattern avoids more complex locking and the race has no practical negative impact.
        foreach (var (key, item) in _pollingItems.ToArray())
        {
            _pollingItems[key] = item with { LastValue = null };
        }
    }

    private static bool ValuesAreEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;

        // Handle arrays by comparing elements
        if (a is Array arrayA && b is Array arrayB)
        {
            if (arrayA.Length != arrayB.Length)
                return false;

            for (int i = 0; i < arrayA.Length; i++)
            {
                if (!Equals(arrayA.GetValue(i), arrayB.GetValue(i)))
                    return false;
            }
            return true;
        }

        return Equals(a, b);
    }

    private async Task ReadBatchAsync(Session session, ArraySegment<PollingItem> batch, CancellationToken cancellationToken)
    {
        try
        {
            // Build read request
            var nodesToRead = new ReadValueIdCollection();
            foreach (var item in batch)
            {
                nodesToRead.Add(new ReadValueId
                {
                    NodeId = item.NodeId,
                    AttributeId = Opc.Ua.Attributes.Value
                });
            }

            // Execute read
            var response = await session.ReadAsync(
                requestHeader: null,
                maxAge: 0,
                timestampsToReturn: TimestampsToReturn.Both,
                nodesToRead,
                cancellationToken);

            Interlocked.Increment(ref _totalReads);

            // Process results
            for (var i = 0; i < response.Results.Count && i < batch.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dataValue = response.Results[i];
                var pollingItem = batch[i];

                if (StatusCode.IsGood(dataValue.StatusCode))
                {
                    ProcessValueChange(pollingItem, dataValue, DateTimeOffset.UtcNow);
                }
                else if (StatusCode.IsBad(dataValue.StatusCode))
                {
                    Interlocked.Increment(ref _failedReads);
                    _logger.LogWarning("Polling read failed for {NodeId}: {Status}",
                        pollingItem.NodeId, dataValue.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedReads);
            _logger.LogError(ex, "Failed to read batch of {Count} polled items", batch.Count);
        }
    }

    private void ProcessValueChange(PollingItem pollingItem, DataValue dataValue, DateTimeOffset receivedTimestamp)
    {
        var newValue = dataValue.Value;
        var oldValue = pollingItem.LastValue;

        // Only notify on actual change (same as subscription behavior)
        if (!ValuesAreEqual(newValue, oldValue))
        {
            // Update cached value atomically - only if item still exists and hasn't changed
            // This prevents resurrection of items removed between snapshot and processing
            var key = pollingItem.NodeId.ToString();
            var updatedItem = pollingItem with { LastValue = newValue };

            if (!_pollingItems.TryUpdate(key, updatedItem, pollingItem))
            {
                // Item was removed or modified concurrently - skip notification
                _logger.LogTrace("Skipping update for concurrently modified/removed item {NodeId}", pollingItem.NodeId);
                return;
            }

            // Create update record (same pattern as subscription manager)
            var update = new OpcUaPropertyUpdate
            {
                Property = pollingItem.Property,
                Value = newValue,
                Timestamp = dataValue.SourceTimestamp
            };

            // Queue update using same pattern as subscriptions
            var state = (source: this, update, receivedTimestamp);
            _updater.EnqueueOrApplyUpdate(state, static s =>
            {
                try
                {
                    s.update.Property.SetValueFromSource(s.source, s.update.Timestamp, s.receivedTimestamp, s.update.Value);
                }
                catch (Exception e)
                {
                    s.source._logger.LogError(e, "Failed to apply polled value change for {Path}", s.update.Property.Name);
                }
            });

            Interlocked.Increment(ref _valueChanges);

            _logger.LogTrace("Polled value changed for {NodeId}: {OldValue} -> {NewValue}",
                pollingItem.NodeId, oldValue, newValue);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _logger.LogDebug("Disposing OPC UA polling manager (Total reads: {TotalReads}, Failed: {FailedReads}, Value changes: {ValueChanges}, Slow polls: {SlowPolls}, Circuit breaker trips: {Trips})",
            _totalReads, _failedReads, _valueChanges, _slowPolls, _circuitBreakerTrips);

        // Stop timer and cancel work
        _timer.Dispose();
        _cts.Cancel();

        // Wait for polling task to complete (with timeout)
        try
        {
            if (_pollingTask != null && !_pollingTask.Wait(_disposalTimeout))
            {
                _logger.LogWarning("Polling task did not complete within {Timeout} timeout", _disposalTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during cancellation
        }

        _cts.Dispose();
        _pollingItems.Clear();
    }

    private record struct PollingItem(
        NodeId NodeId,
        RegisteredSubjectProperty Property,
        object? LastValue
    );
}
