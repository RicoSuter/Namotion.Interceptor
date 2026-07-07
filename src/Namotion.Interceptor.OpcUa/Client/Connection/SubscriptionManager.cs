using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Performance;
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
    private readonly IReadAfterWriteRegistrar? _readAfterWriteManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();

    private volatile bool _shuttingDown; // Prevents new callbacks during cleanup
    private volatile bool _callbacksEnabled; // Gated to false until subscription setup completes

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
        IReadAfterWriteRegistrar? readAfterWriteManager,
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
        // Close the callback gate first so a reconnection re-setup cannot let an in-flight or
        // newly-entering notification pass on the previous setup's stale-true flag. The gate
        // reopens only as the final statement, after the detached-subject sweep.
        _callbacksEnabled = false;

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
        var maxItemsPerSubscription = _configuration.MaxItemsPerSubscription;
        for (var i = 0; i < itemCount; i += maxItemsPerSubscription)
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
                MaxNotificationsPerPublish = _configuration.SubscriptionMaxNotificationsPerPublish,
                RepublishAfterTransfer = true, // Enable SDK's automatic republish of missed messages after transfer
                SequentialPublishing = _configuration.SubscriptionSequentialPublishing,
            };

            if (!session.AddSubscription(subscription))
            {
                throw new InvalidOperationException("Failed to add OPC UA subscription.");
            }

            subscription.FastDataChangeCallback += OnFastDataChange;
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);

            var batchEnd = Math.Min(i + maxItemsPerSubscription, itemCount);
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

            // Add to collection AFTER initialization (temporal separation - health monitor never sees partial state)
            _subscriptions.TryAdd(subscription, 0);
        }

        // Sweep before read-after-write registration so a subject that detached during setup
        // is never registered. Callbacks stay gated until after both the sweep and survivor
        // registration complete, so no notification can reach a subject during setup.
        SweepDetachedSubjects();

        RegisterSurvivors(_subscriptions.Keys.SelectMany(s => s.MonitoredItems).ToArray());

        // Open the gate only after the sweep and survivor registration are complete.
        _callbacksEnabled = true;
    }

    private void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        if (_shuttingDown || !_callbacksEnabled)
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
            _source.IncomingThroughput.Add(changes.Count);

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

                var statusCode = monitoredItem.Status?.Error?.StatusCode ?? StatusCodes.Good;

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
    /// Removes monitored items for every subject that is no longer in the registry.
    /// Returns the deduplicated list of subjects that were swept.
    /// </summary>
    private IReadOnlyList<IInterceptorSubject> SweepDetachedSubjects()
    {
        var swept = new HashSet<IInterceptorSubject>();
        foreach (var property in _monitoredItems.Values)
        {
            var subject = property.Reference.Subject;
            if (subject.TryGetRegisteredSubject() is null && swept.Add(subject))
            {
                RemoveItemsForSubject(subject);
                _pollingManager?.RemoveItemsForSubject(subject);
            }
        }

        return swept.Count == 0
            ? Array.Empty<IInterceptorSubject>()
            : [.. swept];
    }

    /// <summary>
    /// Registers read-after-write tracking for monitored items whose subject survived the sweep.
    /// Only items that are created on the server and still present in <c>_monitoredItems</c>
    /// (the sweep removed detached subjects' handles) are registered.
    /// Returns the client handles that were registered.
    /// </summary>
    private IReadOnlyList<uint> RegisterSurvivors(IReadOnlyCollection<MonitoredItem> monitoredItems)
    {
        if (_readAfterWriteManager is null)
        {
            return Array.Empty<uint>();
        }

        var registered = new List<uint>();
        foreach (var item in monitoredItems)
        {
            if (item is { Handle: RegisteredSubjectProperty property, Status.Created: true } &&
                _monitoredItems.ContainsKey(item.ClientHandle))
            {
                _readAfterWriteManager.RegisterProperty(
                    item.StartNodeId,
                    property,
                    GetRequestedSamplingInterval(property),
                    TimeSpan.FromMilliseconds(item.Status.SamplingInterval));
                registered.Add(item.ClientHandle);
            }
        }

        return registered;
    }

    /// <summary>
    /// Gets the requested sampling interval for a property from the mapper or configuration default.
    /// </summary>
    private int? GetRequestedSamplingInterval(RegisteredSubjectProperty property)
    {
        if (_configuration.Mapper.TryGetMapping(property, _source.RootSubject, out var mapping) &&
            mapping.SamplingInterval.HasValue)
        {
            return mapping.SamplingInterval;
        }

        return _configuration.DefaultSamplingInterval;
    }

    /// <summary>
    /// Applies a single data change for the given client handle through the property writer,
    /// mirroring the per-item logic of OnFastDataChange. No-ops when gated or shutting down,
    /// and when the handle is not in the monitored items dictionary.
    /// Intended as a unit-testable seam: it exercises the same gate flag as OnFastDataChange
    /// without requiring a live OPC UA SDK Subscription.
    /// </summary>
    internal void ApplyDataChange(uint clientHandle, DateTimeOffset timestamp, object? value)
    {
        if (_shuttingDown || !_callbacksEnabled)
        {
            return;
        }

        if (!_monitoredItems.TryGetValue(clientHandle, out var property))
        {
            return;
        }

        var receivedTimestamp = DateTimeOffset.UtcNow;
        var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(value, property);
        var state = (source: _source, property, timestamp, receivedTimestamp, convertedValue, logger: _logger);
        _propertyWriter.Write(state, static s =>
        {
            try
            {
                s.property.SetValueFromSource(s.source, s.timestamp, s.receivedTimestamp, s.convertedValue);
            }
            catch (Exception e)
            {
                s.logger.LogError(e, "Failed to apply change for property {PropertyName}.", s.property.Name);
            }
        });
    }

    internal bool AreCallbacksEnabledForTesting => _callbacksEnabled;
    internal void EnableCallbacksForTesting() => _callbacksEnabled = true;
    internal IDictionary<uint, RegisteredSubjectProperty> MonitoredItemsForTesting => _monitoredItems;

    internal IReadOnlyList<IInterceptorSubject> SweepDetachedSubjectsForTesting() => SweepDetachedSubjects();
    internal IReadOnlyList<uint> RegisterSurvivorsForReadAfterWriteForTesting(IReadOnlyCollection<MonitoredItem> monitoredItems) => RegisterSurvivors(monitoredItems);

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;
        _callbacksEnabled = false;

        var subscriptions = _subscriptions.Keys.ToArray();
        _subscriptions.Clear();

        foreach (var subscription in subscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
        }

        // Use session.RemoveSubscriptionsAsync instead of subscription.DeleteAsync
        // to also remove subscriptions from session.m_subscriptions. DeleteAsync alone
        // only deletes on the server but does not remove from the session's internal list,
        // keeping the entire Subscription object graph alive until session disposal.
        if (subscriptions.Length > 0)
        {
            var session = subscriptions[0].Session;
            if (session != null)
            {
                var disposalTimeout = _configuration.SessionDisposalTimeout;
                try
                {
                    await session.RemoveSubscriptionsAsync(subscriptions, CancellationToken.None)
                        .WaitAsync(disposalTimeout).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove subscriptions during disposal.");
                }
            }
        }
    }
}
