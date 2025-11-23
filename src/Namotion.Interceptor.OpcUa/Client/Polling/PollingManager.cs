using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Polling;

/// <summary>
/// Polling fallback for nodes that don't support subscriptions. Circuit breaker prevents resource exhaustion.
/// Thread-safe. Start() is idempotent. Values reset on reconnection (some data loss during disconnection is expected).
/// </summary>
internal sealed class PollingManager : IDisposable
{
    private readonly OpcUaClientSource _connector;
    private readonly ILogger _logger;
    private readonly SessionManager _sessionManager;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly PollingCircuitBreaker _circuitBreaker;
    private readonly PollingMetrics _metrics = new();

    private readonly ConcurrentDictionary<string, PollingItem> _pollingItems = new();
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _startLock = new();

    private Task? _pollingTask;
    private ISession? _lastKnownSession;
    private int _disposed;

    public PollingManager(OpcUaClientSource connector,
        SessionManager sessionManager,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(propertyWriter);

        _connector = connector;
        _logger = logger;
        _sessionManager = sessionManager;
        _propertyWriter = propertyWriter;
        _configuration = configuration;

        _circuitBreaker = new PollingCircuitBreaker(configuration.PollingCircuitBreakerThreshold, configuration.PollingCircuitBreakerCooldown);
        _timer = new PeriodicTimer(configuration.PollingInterval);
    }

    /// <summary>
    /// Gets the number of items currently being polled.
    /// </summary>
    public int PollingItemCount => _pollingItems.Count;

    /// <summary>
    /// Gets the total number of successful read operations performed.
    /// </summary>
    public long TotalReads => _metrics.TotalReads;

    /// <summary>
    /// Gets the total number of failed read operations.
    /// </summary>
    public long FailedReads => _metrics.FailedReads;

    /// <summary>
    /// Gets the total number of value changes detected and processed.
    /// </summary>
    public long ValueChanges => _metrics.ValueChanges;

    /// <summary>
    /// Gets the total number of slow polls (poll duration exceeded polling interval).
    /// </summary>
    public long SlowPolls => _metrics.SlowPolls;

    /// <summary>
    /// Gets the total number of times the circuit breaker has tripped due to persistent failures.
    /// </summary>
    public long CircuitBreakerTrips => _circuitBreaker.TripCount;

    /// <summary>
    /// Gets whether the circuit breaker is currently open (polling suspended due to persistent failures).
    /// </summary>
    public bool IsCircuitOpen => _circuitBreaker.IsOpen;

    /// <summary>
    /// Gets whether the polling manager is currently running.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _pollingTask) != null && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Starts the polling loop in the background.
    /// Idempotent - safe to call multiple times (subsequent calls are ignored).
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if already disposed</exception>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(PollingManager));

        lock (_startLock)
        {
            if (Volatile.Read(ref _pollingTask) != null)
            {
                _logger.LogDebug("Polling manager already started, ignoring duplicate start request");
                return;
            }

            var task = Task.Run(async () => await PollLoopAsync(_cts.Token));
            Volatile.Write(ref _pollingTask, task); // Ensure task assignment is visible to all threads
        }

        _logger.LogInformation("OPC UA polling manager started with interval {Interval}ms", _configuration.PollingInterval.TotalMilliseconds);
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
            while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PollItemsAsync(cancellationToken).ConfigureAwait(false);
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

        // Check circuit breaker
        if (!_circuitBreaker.ShouldAttempt())
        {
            var remaining = _circuitBreaker.GetCooldownRemaining();
            _logger.LogDebug("Circuit breaker is open, skipping poll (cooldown: {Remaining}s remaining)",
                (int)remaining.TotalSeconds);
            return;
        }

        var session = _sessionManager.CurrentSession;
        if (session is null || !session.Connected)
        {
            _logger.LogDebug("No active session available for polling (null: {IsNull}, connected: {Connected})",
                session is null, session?.Connected);
            Volatile.Write(ref _lastKnownSession, null);
            return;
        }

        // Detect session change (reconnection)
        var lastSession = Volatile.Read(ref _lastKnownSession);
        if (!ReferenceEquals(lastSession, session))
        {
            _logger.LogInformation("Session change detected, resetting polled item values and circuit breaker");
            ResetPolledValues();
            _circuitBreaker.Reset();
        }
        Volatile.Write(ref _lastKnownSession, session);

        var startTime = DateTimeOffset.UtcNow;
        var pollSucceeded = false;

        try
        {
            // Get snapshot of items to poll - creates a copy to avoid concurrent modifications
            var itemsToRead = _pollingItems.Values.ToArray();

            // Process in batches using direct indexing
            for (int i = 0; i < itemsToRead.Length; i += _configuration.PollingBatchSize)
            {
                var batchSize = Math.Min(_configuration.PollingBatchSize, itemsToRead.Length - i);
                var batch = new ArraySegment<PollingItem>(itemsToRead, i, batchSize);
                await ReadBatchAsync(session, batch, cancellationToken).ConfigureAwait(false);
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
            // Update circuit breaker
            if (pollSucceeded)
            {
                _circuitBreaker.RecordSuccess();
            }
            else if (_circuitBreaker.RecordFailure())
            {
                _logger.LogError("Circuit breaker opened after consecutive failures. Polling suspended temporarily.");
            }

            // Detect slow polls that exceed the polling interval
            var duration = DateTimeOffset.UtcNow - startTime;
            if (duration > _configuration.PollingInterval)
            {
                _metrics.RecordSlowPoll();
                _logger.LogWarning("Slow poll detected: polling took {Duration}ms, which exceeds interval of {Interval}ms. Consider increasing polling interval or batch size.",
                    duration.TotalMilliseconds, _configuration.PollingInterval.TotalMilliseconds);
            }
        }
    }

    private void ResetPolledValues()
    {
        // Clear cached values on session change to force re-notification
        // Take snapshot to avoid TOCTOU issues with concurrent modifications
        // Use TryUpdate to handle concurrent removal safely (matching pattern in ProcessValueChange)
        foreach (var (key, item) in _pollingItems.ToArray())
        {
            _pollingItems.TryUpdate(key, item with { LastValue = null }, item);
            // If TryUpdate fails, item was removed or modified concurrently - skip silently
        }
    }

    private static bool ValuesAreEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;

        // Handle arrays using StructuralComparisons (avoids boxing for primitive arrays)
        if (a is Array arrayA && b is Array arrayB)
        {
            return StructuralComparisons.StructuralEqualityComparer.Equals(arrayA, arrayB);
        }

        return Equals(a, b);
    }

    private async Task ReadBatchAsync(Session session, ArraySegment<PollingItem> batch, CancellationToken cancellationToken)
    {
        try
        {
            // Build read request - pre-size to avoid resizing
            var nodesToRead = new ReadValueIdCollection(batch.Count);
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
                cancellationToken).ConfigureAwait(false);

            // Process results - count metrics per item for accurate monitoring
            for (var i = 0; i < Math.Min(response.Results.Count, batch.Count); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dataValue = response.Results[i];
                var pollingItem = batch[i];

                if (StatusCode.IsGood(dataValue.StatusCode))
                {
                    _metrics.RecordRead();
                    ProcessValueChange(pollingItem, dataValue, DateTimeOffset.UtcNow);
                }
                else if (StatusCode.IsBad(dataValue.StatusCode))
                {
                    _metrics.RecordFailedRead();
                    _logger.LogWarning("Polling read failed for {NodeId}: {Status}",
                        pollingItem.NodeId, dataValue.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            // Batch-level failure - count all items in batch as failed
            for (var i = 0; i < batch.Count; i++)
            {
                _metrics.RecordFailedRead();
            }
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
            var update = new PropertyUpdate
            {
                Property = pollingItem.Property,
                Value = newValue,
                Timestamp = dataValue.SourceTimestamp
            };

            // Queue update using same pattern as subscriptions
            var state = (connector: _connector, update, receivedTimestamp, logger: _logger);
            _propertyWriter.Write(state, static s =>
            {
                try
                {
                    s.update.Property.SetValueFromSource(s.connector, s.update.Timestamp, s.receivedTimestamp, s.update.Value);
                }
                catch (Exception e)
                {
                    s.logger.LogError(e, "Failed to apply polled value change for {Path}", s.update.Property.Name);
                }
            });

            _metrics.RecordValueChange();

            _logger.LogTrace("Polled value changed for {NodeId}: {OldValue} -> {NewValue}",
                pollingItem.NodeId, oldValue, newValue);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _logger.LogDebug("Disposing OPC UA polling manager (Total reads: {TotalReads}, Failed: {FailedReads}, Value changes: {ValueChanges}, Slow polls: {SlowPolls}, Circuit breaker trips: {Trips})",
            _metrics.TotalReads, _metrics.FailedReads, _metrics.ValueChanges, _metrics.SlowPolls, _circuitBreaker.TripCount);

        // Stop timer and cancel work
        _timer.Dispose();
        _cts.Cancel();

        // Wait for polling task to complete (with timeout)
        try
        {
            if (_pollingTask != null && !_pollingTask.Wait(_configuration.PollingDisposalTimeout))
            {
                _logger.LogWarning("Polling task did not complete within {Timeout} timeout", _configuration.PollingDisposalTimeout);
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
