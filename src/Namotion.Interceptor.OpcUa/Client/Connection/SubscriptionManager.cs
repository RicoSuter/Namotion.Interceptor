using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Performance;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// How a monitored item that failed to (re-)create should be handled, aligned with
/// <see cref="SubscriptionHealthMonitor.IsRetryable(Opc.Ua.Client.MonitoredItem)"/>.
/// </summary>
internal enum FailedMonitoredItemDisposition
{
    /// <summary>Transient failure: leave the item in the subscription so the health monitor retries it.</summary>
    KeepForRetry,

    /// <summary>Node does not support subscriptions: move it to the polling fallback.</summary>
    FallbackToPolling,

    /// <summary>Permanent failure: remove the item; retrying cannot succeed.</summary>
    Drop
}

/// <summary>
/// What to do with a retryable monitored item that keeps failing to heal.
/// </summary>
internal enum HealDecision
{
    /// <summary>Still within the retry bound: let the health monitor keep retrying.</summary>
    KeepRetrying,

    /// <summary>Retry bound exceeded and polling available: move the item to the polling fallback.</summary>
    EscalateToPolling
}

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
    private readonly ConcurrentDictionary<uint, int> _healAttempts = new();

    // Consecutive failed heal ticks a retryable item tolerates before it is escalated to polling
    // instead of being retried forever. With polling disabled there is no escalation target, so the
    // item keeps being retried and self-heals once the node recovers.
    internal const int MaxHealAttemptsBeforeEscalation = 3;

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
        _healAttempts.Clear();

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
        List<MonitoredItem>? removedItems = null;
        List<MonitoredItem>? polledItems = null;
        var keptForRetry = 0;

        var pollingEnabled = _configuration.EnablePollingFallback && _pollingManager != null;

        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            if (!SubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
            {
                continue;
            }

            var statusCode = monitoredItem.Status.Error?.StatusCode ?? StatusCodes.Good;

            switch (ClassifyFailedItem(statusCode, pollingEnabled))
            {
                case FailedMonitoredItemDisposition.KeepForRetry:
                    // Leave it in the subscription and in _monitoredItems so the health monitor
                    // heals it. Removing it here silently orphaned transiently-failed items.
                    keptForRetry++;
                    _logger.LogWarning("OPC UA monitored item {DisplayName} failed transiently ({Status}); keeping it for the health monitor to retry.",
                        monitoredItem.DisplayName, statusCode);
                    break;

                case FailedMonitoredItemDisposition.FallbackToPolling:
                    removedItems ??= [];
                    removedItems.Add(monitoredItem);
                    _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);
                    polledItems ??= [];
                    polledItems.Add(monitoredItem);
                    _logger.LogWarning("Monitored item {DisplayName} does not support subscriptions ({Status}), falling back to polling",
                        monitoredItem.DisplayName, statusCode);
                    break;

                case FailedMonitoredItemDisposition.Drop:
                    removedItems ??= [];
                    removedItems.Add(monitoredItem);
                    _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);
                    _logger.LogError("OPC UA monitored item creation failed permanently for {DisplayName} (Handle={Handle}): {Status}",
                        monitoredItem.DisplayName, monitoredItem.ClientHandle, statusCode);
                    break;
            }
        }

        if (removedItems?.Count > 0)
        {
            foreach (var monitoredItem in removedItems)
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

            if (polledItems?.Count > 0 && _pollingManager != null)
            {
                foreach (var item in polledItems)
                {
                    _pollingManager.AddItem(item);
                }
            }
        }

        if (removedItems?.Count > 0 || keptForRetry > 0)
        {
            _logger.LogWarning(
                "Subscription {SubscriptionId}: removed {Removed} failed monitored items " +
                "({Polled} switched to polling), kept {Kept} for the health monitor to retry.",
                subscription.Id, removedItems?.Count ?? 0, polledItems?.Count ?? 0, keptForRetry);
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
    /// Decides how a failed monitored item should be handled. Transient failures are kept in the
    /// subscription so <see cref="SubscriptionHealthMonitor"/> can heal them (previously they were
    /// dropped, which silently orphaned the item until an unrelated full reconnect).
    /// </summary>
    internal static FailedMonitoredItemDisposition ClassifyFailedItem(StatusCode statusCode, bool pollingEnabled)
    {
        if (IsSubscriptionUnsupported(statusCode))
        {
            return pollingEnabled
                ? FailedMonitoredItemDisposition.FallbackToPolling
                : FailedMonitoredItemDisposition.Drop;
        }

        return SubscriptionHealthMonitor.IsRetryable(statusCode)
            ? FailedMonitoredItemDisposition.KeepForRetry
            : FailedMonitoredItemDisposition.Drop;
    }

    /// <summary>
    /// Decides what to do with a retryable item that keeps failing to heal, once polling fallback
    /// is available: keep retrying within the bound, then escalate to polling. With polling disabled
    /// the caller never escalates, so the health monitor keeps retrying and the item self-heals.
    /// </summary>
    internal static HealDecision DecideHealAction(int consecutiveFailures, int maxAttempts)
    {
        return consecutiveFailures < maxAttempts
            ? HealDecision.KeepRetrying
            : HealDecision.EscalateToPolling;
    }

    /// <summary>
    /// Escalates retryable monitored items that keep failing after the health monitor has retried
    /// them. An item that exceeds <see cref="MaxHealAttemptsBeforeEscalation"/> consecutive failed
    /// heals falls back to polling instead of being retried forever. With polling disabled there is
    /// no better target, so the item is never dropped: the health monitor keeps retrying and it
    /// self-heals once the node recovers. Items that recover reset their attempt count, and reconnect
    /// re-attempts a real subscription for every owned item, so escalation is not permanent.
    /// </summary>
    public async Task EscalatePersistentlyFailedItemsAsync(CancellationToken cancellationToken)
    {
        // Escalation only has a target when polling is available. With polling disabled there is
        // nothing better than letting the health monitor keep retrying, which self-heals once the
        // node recovers, so a retryable item is never dropped.
        if (!_configuration.EnablePollingFallback || _pollingManager is null)
        {
            return;
        }

        foreach (var subscription in _subscriptions.Keys)
        {
            List<MonitoredItem>? toEscalate = null;

            foreach (var monitoredItem in subscription.MonitoredItems)
            {
                if (!SubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
                {
                    if (!_healAttempts.IsEmpty)
                    {
                        _healAttempts.TryRemove(monitoredItem.ClientHandle, out _); // recovered: reset
                    }
                    continue;
                }

                if (!SubscriptionHealthMonitor.IsRetryable(monitoredItem))
                {
                    continue; // permanent errors are handled at creation time
                }

                var attempts = _healAttempts.AddOrUpdate(monitoredItem.ClientHandle, 1, static (_, current) => current + 1);

                if (DecideHealAction(attempts, MaxHealAttemptsBeforeEscalation) == HealDecision.EscalateToPolling)
                {
                    (toEscalate ??= []).Add(monitoredItem);
                }
            }

            if (toEscalate is not { Count: > 0 })
            {
                continue;
            }

            foreach (var monitoredItem in toEscalate)
            {
                _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);
                _healAttempts.TryRemove(monitoredItem.ClientHandle, out _);
                subscription.RemoveItem(monitoredItem);
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyChanges after escalating persistently-failed OPC UA monitored items to polling failed.");
            }

            foreach (var monitoredItem in toEscalate)
            {
                _pollingManager.AddItem(monitoredItem);
            }

            _logger.LogWarning(
                "Escalated {Count} persistently-failing monitored items to polling in subscription {SubscriptionId} after {Max} retries.",
                toEscalate.Count, subscription.Id, MaxHealAttemptsBeforeEscalation);
        }
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
                _healAttempts.TryRemove(kvp.Key, out _);
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

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;

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
                    await session.RemoveSubscriptionsAsync(subscriptions, default)
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
