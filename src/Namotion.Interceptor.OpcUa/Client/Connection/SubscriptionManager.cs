using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

internal class SubscriptionManager : IAsyncDisposable
{
    private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool
        = new(() => new List<PropertyUpdate>(16));

    private readonly OpcUaSubjectClientSource _source;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly PollingManager? _pollingManager;
    private readonly ReadAfterWriteManager? _readAfterWriteManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();

    private volatile bool _shuttingDown; // Prevents new callbacks during cleanup

    /// <summary>
    /// Gets the current list of subscriptions (thread-safe collection).
    /// </summary>
    public IReadOnlyCollection<Subscription> Subscriptions => (IReadOnlyCollection<Subscription>)_subscriptions.Keys;

    /// <summary>
    /// Gets the current monitored items (thread-safe dictionary).
    /// </summary>
    public IReadOnlyDictionary<uint, RegisteredSubjectProperty> MonitoredItems => _monitoredItems;

    /// <summary>
    /// Returns true if any active subscription has stopped receiving publish responses from the server.
    /// </summary>
    public bool HasStoppedPublishing
    {
        get
        {
            foreach (var subscription in _subscriptions.Keys)
            {
                if (subscription.PublishingStopped)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public SubscriptionManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        PollingManager? pollingManager,
        ReadAfterWriteManager? readAfterWriteManager,
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        _source = source;
        _propertyWriter = propertyWriter;
        _pollingManager = pollingManager;
        _readAfterWriteManager = readAfterWriteManager;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task CreateBatchedSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        // Clear any existing subscriptions and monitored items from previous session (reconnection scenario).
        // Old subscriptions are orphaned (belong to dead session), so we just need to remove our references.
        foreach (var oldSubscription in _subscriptions.Keys)
        {
            oldSubscription.FastDataChangeCallback -= OnFastDataChange;
        }
        _subscriptions.Clear();
        _monitoredItems.Clear();

        // Reset shutdown flag AFTER clearing collections - prevents old callbacks from processing
        // during the window between flag reset and collection clearing (defense-in-depth).
        _shuttingDown = false;

        var itemCount = monitoredItems.Count;
        var maximumItemsPerSubscription = _configuration.MaximumItemsPerSubscription;
        for (var i = 0; i < itemCount; i += maximumItemsPerSubscription)
        {
            var subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingEnabled = true,
                PublishingInterval = _configuration.DefaultPublishingInterval,
                DisableMonitoredItemCache = true, // not needed as we use fast data change callback
                MinLifetimeInterval = 60_000,
                KeepAliveCount = _configuration.SubscriptionKeepAliveCount,
                LifetimeCount = _configuration.SubscriptionLifetimeCount,
                Priority = _configuration.SubscriptionPriority,
                MaxNotificationsPerPublish = _configuration.SubscriptionMaximumNotificationsPerPublish,
                RepublishAfterTransfer = true, // Enable SDK's automatic republish of missed messages after transfer
                SequentialPublishing = _configuration.SubscriptionSequentialPublishing,
            };

            if (!session.AddSubscription(subscription))
            {
                throw new InvalidOperationException("Failed to add OPC UA subscription.");
            }

            subscription.FastDataChangeCallback += OnFastDataChange;
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);

            var batchEnd = Math.Min(i + maximumItemsPerSubscription, itemCount);
            for (var j = i; j < batchEnd; j++)
            {
                var item = monitoredItems[j];
                subscription.AddItem(item);

                if (item.Handle is RegisteredSubjectProperty property)
                {
                    _monitoredItems[item.ClientHandle] = property;
                }
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResultException sre)
            {
                _logger.LogWarning(sre, "ApplyChanges failed for a batch; attempting to keep valid OPC UA monitored items by removing failed ones.");
            }

            await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken).ConfigureAwait(false);

            // Register properties with ReadAfterWriteManager now that we know revised sampling intervals
            RegisterPropertiesWithReadAfterWriteManager(subscription);

            // Add to collection AFTER initialization (temporal separation - health monitor never sees partial state)
            _subscriptions.TryAdd(subscription, 0);
        }
    }

    private void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        if (_shuttingDown)
        {
            return;
        }

        var monitoredItemsCount = notification.MonitoredItems.Count;
        if (monitoredItemsCount == 0)
        {
            return;
        }

        var receivedTimestamp = DateTimeOffset.UtcNow;
        var changes = ChangesPool.Rent();

        try
        {
            for (var i = 0; i < monitoredItemsCount; i++)
            {
                var item = notification.MonitoredItems[i];
                if (_monitoredItems.TryGetValue(item.ClientHandle, out var property))
                {
                    changes.Add(new PropertyUpdate
                    {
                        Property = property,
                        Value = _configuration.ValueConverter.ConvertToPropertyValue(item.Value.Value, property),
                        Timestamp = item.Value.SourceTimestamp
                    });
                }
            }
        }
        catch
        {
            // Return pooled list on exception to prevent pool exhaustion
            changes.Clear();
            ChangesPool.Return(changes);
            throw;
        }

        if (changes.Count > 0)
        {
            // Pool item returned inside callback. Safe because ApplyUpdate never throws:
            // It wraps callback execution in try-catch and only throws on catastrophic failures (lock/memory corruption).
            var state = (source: _source, subscription, receivedTimestamp, changes, logger: _logger);
            _propertyWriter.Write(state, static s =>
            {
                for (var i = 0; i < s.changes.Count; i++)
                {
                    var change = s.changes[i];
                    try
                    {
                        change.Property.SetValueFromSource(s.source, change.Timestamp, s.receivedTimestamp, change.Value);
                    }
                    catch (Exception e)
                    {
                        s.logger.LogError(e, "Failed to apply change for property {PropertyName}.", change.Property.Name);
                    }
                }

                s.changes.Clear();
                ChangesPool.Return(s.changes);
            });
        }
        else
        {
            ChangesPool.Return(changes);
        }
    }

    /// <summary>
    /// Updates the subscription list to reference subscriptions transferred by SessionReconnectHandler.
    /// Called after successful session transfer to embrace OPC Foundation's subscription preservation.
    /// </summary>
    public void UpdateTransferredSubscriptions(IReadOnlyCollection<Subscription> transferredSubscriptions)
    {
        var oldSubscriptions = _subscriptions.Keys.ToArray();
        foreach (var subscription in transferredSubscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.FastDataChangeCallback += OnFastDataChange;
            _subscriptions.TryAdd(subscription, 0);
        }

        foreach (var oldSubscription in oldSubscriptions)
        {
            _subscriptions.TryRemove(oldSubscription, out _);
            oldSubscription.FastDataChangeCallback -= OnFastDataChange;
        }


        _logger.LogInformation("Updated subscription manager with {Count} transferred subscriptions (removed {OldCount} old)",
            transferredSubscriptions.Count, oldSubscriptions.Length);
    }

    private async Task FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        List<MonitoredItem>? failedItems = null;
        List<MonitoredItem>? polledItems = null;

        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            if (SubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
            {
                failedItems ??= [];
                failedItems.Add(monitoredItem);

                _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);

                var statusCode = monitoredItem.Status.Error?.StatusCode ?? StatusCodes.Good;

                // Check if we should fall back to polling for this item
                if (_configuration.EnablePollingFallback &&
                    _pollingManager != null &&
                    IsSubscriptionUnsupported(statusCode))
                {
                    polledItems ??= [];
                    polledItems.Add(monitoredItem);
                    _logger.LogWarning("Monitored item {DisplayName} does not support subscriptions ({Status}), falling back to polling",
                        monitoredItem.DisplayName, statusCode);
                }
                else
                {
                    _logger.LogError("OPC UA monitored item creation failed for {DisplayName} (Handle={Handle}): {Status}",
                        monitoredItem.DisplayName, monitoredItem.ClientHandle, statusCode);
                }
            }
        }

        if (failedItems?.Count > 0)
        {
            foreach (var monitoredItem in failedItems)
            {
                subscription.RemoveItem(monitoredItem);
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyChanges after removing failed items still failed. Continuing with remaining OPC UA monitored items.");
            }

            // Add items that support polling to polling manager
            if (polledItems?.Count > 0 && _pollingManager != null)
            {
                foreach (var item in polledItems)
                {
                    _pollingManager.AddItem(item);
                }
            }

            var failedCount = failedItems.Count;
            var polledCount = polledItems?.Count;
            if (polledCount > 0)
            {
                _logger.LogWarning(
                    "Removed {Removed} failed monitored items from subscription " +
                    "{SubscriptionId}. {Polled} items switched to polling fallback.",
                    failedCount, subscription.Id, polledCount);
            }
            else
            {
                _logger.LogWarning(
                    "Removed {Removed} failed monitored items " +
                    "from subscription {SubscriptionId}.",
                    failedCount, subscription.Id);
            }
        }
    }

    /// <summary>
    /// Checks if a status code indicates that subscriptions are not supported for this node.
    /// These items should fall back to polling if enabled.
    /// </summary>
    private static bool IsSubscriptionUnsupported(StatusCode statusCode)
    {
        // BadNotSupported - Server doesn't support subscriptions for this node
        // BadMonitoredItemFilterUnsupported - Filter not supported (data change filter)
        // Note: BadAttributeIdInvalid is a permanent error - polling won't work either, so excluded
        return statusCode == StatusCodes.BadNotSupported ||
               statusCode == StatusCodes.BadMonitoredItemFilterUnsupported;
    }

    /// <summary>
    /// Removes monitored items for a detached subject. Idempotent.
    /// Note: OPC UA subscription items remain on server until session ends.
    /// This just cleans up local tracking to avoid memory leaks.
    /// </summary>
    public void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        foreach (var kvp in _monitoredItems)
        {
            if (kvp.Value.Reference.Subject == subject)
            {
                _monitoredItems.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Registers all successfully created monitored items with ReadAfterWriteManager.
    /// Called after ApplyChangesAsync when we know the revised sampling intervals.
    /// </summary>
    private void RegisterPropertiesWithReadAfterWriteManager(Subscription subscription)
    {
        if (_readAfterWriteManager is null)
        {
            return;
        }

        foreach (var item in subscription.MonitoredItems)
        {
            if (item.Handle is RegisteredSubjectProperty property && item.Status?.Created == true)
            {
                var requestedInterval = GetRequestedSamplingInterval(property);
                var revisedInterval = TimeSpan.FromMilliseconds(item.Status.SamplingInterval);
                _readAfterWriteManager.RegisterProperty(item.StartNodeId, property, requestedInterval, revisedInterval);
            }
        }
    }

    /// <summary>
    /// Gets the requested sampling interval for a property from its OPC UA attribute or configuration default.
    /// </summary>
    private int? GetRequestedSamplingInterval(RegisteredSubjectProperty property)
    {
        var attribute = property.TryGetOpcUaNodeAttribute();
        return attribute != null && attribute.SamplingInterval != int.MinValue
            ? attribute.SamplingInterval
            : _configuration.DefaultSamplingInterval;
    }

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;

        var subscriptions = _subscriptions.Keys.ToArray();
        _subscriptions.Clear();

        foreach (var subscription in subscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
        }

        var deleteTasks = subscriptions.Select(async subscription =>
        {
            try
            {
                await subscription.DeleteAsync(true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete subscription {SubscriptionId} during disposal.", subscription.Id);
            }
        });

        // Use timeout to prevent indefinite hang if server is unresponsive
        var disposalTimeout = _configuration.SessionDisposalTimeout;
        try
        {
            await Task.WhenAll(deleteTasks).WaitAsync(disposalTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Subscription deletion timed out after {Timeout} during disposal. " +
                "Some subscriptions may not have been cleanly removed from server.",
                disposalTimeout);
        }
    }
}
