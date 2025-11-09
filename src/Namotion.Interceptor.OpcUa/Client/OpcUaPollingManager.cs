using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Manages polling-based reading for OPC UA nodes that don't support subscriptions.
/// Provides automatic fallback when subscription creation fails.
/// </summary>
internal sealed class OpcUaPollingManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly OpcUaSessionManager _sessionManager;
    private ISubjectUpdater? _updater;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;

    private readonly ConcurrentDictionary<string, PollingItem> _pollingItems = new();
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;
    private int _disposed = 0;

    public OpcUaPollingManager(
        ILogger logger,
        OpcUaSessionManager sessionManager,
        TimeSpan pollingInterval,
        int batchSize)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _pollingInterval = pollingInterval;
        _batchSize = batchSize;
        _timer = new PeriodicTimer(pollingInterval);
    }

    /// <summary>
    /// Sets the subject updater for processing polled value changes.
    /// Must be called before starting the polling manager.
    /// </summary>
    public void SetUpdater(ISubjectUpdater updater)
    {
        _updater = updater;
    }

    /// <summary>
    /// Gets the number of items currently being polled.
    /// </summary>
    public int PollingItemCount => _pollingItems.Count;

    /// <summary>
    /// Starts the polling loop in the background.
    /// </summary>
    public void Start()
    {
        _pollingTask = Task.Run(async () => await PollLoopAsync(_cts.Token));
        _logger.LogInformation("OPC UA polling manager started with interval {Interval}ms", _pollingInterval.TotalMilliseconds);
    }

    /// <summary>
    /// Adds a monitored item to polling fallback.
    /// Called when subscription creation fails for this item.
    /// </summary>
    public void AddItem(MonitoredItem monitoredItem)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            return;

        var key = monitoredItem.StartNodeId.ToString();

        // Extract RegisteredSubjectProperty from handle
        if (monitoredItem.Handle is not Namotion.Interceptor.Registry.Abstractions.RegisteredSubjectProperty property)
        {
            _logger.LogWarning("Cannot add item {NodeId} to polling - invalid handle", key);
            return;
        }

        var pollingItem = new PollingItem(
            NodeId: (NodeId)monitoredItem.StartNodeId,
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
        if (_pollingItems.IsEmpty)
            return;

        var session = _sessionManager.CurrentSession;
        if (session == null)
            return; // No session, skip this poll

        try
        {
            // Get snapshot of items to poll
            var itemsToRead = _pollingItems.Values.ToList();

            // Process in batches
            for (int i = 0; i < itemsToRead.Count; i += _batchSize)
            {
                var batch = itemsToRead.Skip(i).Take(_batchSize).ToList();
                await ReadBatchAsync(session, batch, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling items");
        }
    }

    private async Task ReadBatchAsync(Session session, List<PollingItem> batch, CancellationToken cancellationToken)
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

            // Process results
            for (int i = 0; i < response.Results.Count && i < batch.Count; i++)
            {
                var dataValue = response.Results[i];
                var pollingItem = batch[i];

                if (StatusCode.IsGood(dataValue.StatusCode))
                {
                    ProcessValueChange(pollingItem, dataValue, DateTimeOffset.UtcNow);
                }
                else if (StatusCode.IsBad(dataValue.StatusCode))
                {
                    _logger.LogWarning("Polling read failed for {NodeId}: {Status}",
                        pollingItem.NodeId, dataValue.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read batch of {Count} polled items", batch.Count);
        }
    }

    private void ProcessValueChange(PollingItem pollingItem, DataValue dataValue, DateTimeOffset receivedTimestamp)
    {
        if (_updater == null)
            return; // Updater not set yet

        var newValue = dataValue.Value;
        var oldValue = pollingItem.LastValue;

        // Only notify on actual change (same as subscription behavior)
        if (!Equals(newValue, oldValue))
        {
            // Update cached value
            var key = pollingItem.NodeId.ToString();
            var updatedItem = pollingItem with { LastValue = newValue };
            _pollingItems[key] = updatedItem;

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

            _logger.LogTrace("Polled value changed for {NodeId}: {OldValue} → {NewValue}",
                pollingItem.NodeId, oldValue, newValue);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _logger.LogDebug("Disposing OPC UA polling manager");

        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();

        // Wait for polling task to complete (with timeout)
        if (_pollingTask != null)
        {
            try
            {
                _pollingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for polling task to complete");
            }
        }

        _pollingItems.Clear();
    }

    private record PollingItem(
        NodeId NodeId,
        Namotion.Interceptor.Registry.Abstractions.RegisteredSubjectProperty Property,
        object? LastValue
    );
}
