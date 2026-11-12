using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
using Opc.Ua.Client.Subscriptions;
using Opc.Ua.Client.Subscriptions.MonitoredItems;
using MonitoredItemOptions = Opc.Ua.Client.Subscriptions.MonitoredItems.MonitoredItemOptions;
using SubscriptionOptions = Opc.Ua.Client.Subscriptions.SubscriptionOptions;
using SubscriptionState = Opc.Ua.Client.Subscriptions.SubscriptionState;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// How a monitored item that failed to (re-)create should be handled, aligned with
/// <see cref="OpcUaStatusCodeClassifier.IsTransientError"/>.
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

internal class SubscriptionManager : ISubscriptionNotificationHandler, IAsyncDisposable
{
    private static readonly ObjectPool<List<NotificationUpdate>> ChangesPool
        = new(() => new List<NotificationUpdate>(16));

    // Fallback for re-checking whether a subscription's items were applied, in case an apply pass ends
    // without raising a state change.
    private static readonly TimeSpan AppliedCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly OpcUaSubjectClientSource _source;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly PollingManager? _pollingManager;
    private readonly ReadAfterWriteManager? _readAfterWriteManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly Action<Exception> _reportError;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<uint, MonitoredItemState> _monitoredItems = new();
    // Created on the first callback rather than when the subscription is tracked, because the engine
    // raises state changes before Add returns.
    private readonly ConditionalWeakTable<ISubscription, SubscriptionTracking> _tracking = new();
    private readonly ConcurrentDictionary<ISubscription, byte> _subscriptions = new();
    private readonly ConcurrentDictionary<uint, int> _healAttempts = new();
    private readonly Lock _trackingMutationLock = new();

    private int _subscriptionCount;
    private int _monitoredItemCount;
    private long _lastItemName;

    // Consecutive failed heal ticks a retryable item tolerates before it is escalated to polling
    // instead of being retried forever. With polling disabled there is no escalation target, so the
    // item keeps being retried and self-heals once the node recovers.
    internal const int MaxHealAttemptsBeforeEscalation = 3;

    private volatile bool _shuttingDown; // Prevents new callbacks during cleanup

    /// <summary>
    /// Gets the current list of subscriptions (thread-safe collection).
    /// </summary>
    public IReadOnlyCollection<ISubscription> Subscriptions => (IReadOnlyCollection<ISubscription>)_subscriptions.Keys;

    /// <summary>
    /// Gets how many subscriptions are currently held. Counting through <see cref="Subscriptions"/>
    /// would allocate, because the underlying concurrent dictionary snapshots its keys.
    /// </summary>
    public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

    /// <summary>
    /// Gets how many monitored items are currently held without locking their backing dictionary.
    /// </summary>
    public int MonitoredItemCount => Volatile.Read(ref _monitoredItemCount);

    /// <summary>
    /// Returns true if any active subscription has stopped receiving publish responses from the server.
    /// </summary>
    public bool HasStoppedPublishing
    {
        get
        {
            foreach (var subscription in _subscriptions.Keys)
            {
                if (_tracking.TryGetValue(subscription, out var tracking) && tracking.PublishingStopped)
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
        Action<Exception> reportError,
        ILogger logger)
    {
        _source = source;
        _propertyWriter = propertyWriter;
        _pollingManager = pollingManager;
        _readAfterWriteManager = readAfterWriteManager;
        _configuration = configuration;
        _reportError = reportError;
        _logger = logger;
    }

    public async Task CreateBatchedSubscriptionsAsync(
        IReadOnlyList<MonitoredItemRequest> monitoredItems,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (!session.TryGetSubscriptionManager(out var subscriptionManager) || subscriptionManager is null)
        {
            throw new InvalidOperationException(
                "The OPC UA session does not use the SDK's default subscription engine, which the client requires.");
        }

        // The session outlives a recreate, so subscriptions from a previous call are still registered
        // on it and have to be removed before their replacements are created.
        var previousSubscriptions = _subscriptions.Keys.ToArray();
        ClearTrackedCollections();
        await DisposeSubscriptionsAsync(previousSubscriptions).ConfigureAwait(false);

        _healAttempts.Clear();
        // Re-attempt every owned property as a real subscription; failed nodes are re-added to
        // polling. Prevents double delivery of an escalated item that later recovers.
        _pollingManager?.Clear();

        // Reset shutdown flag AFTER clearing collections - prevents old callbacks from processing
        // during the window between flag reset and collection clearing (defense-in-depth).
        _shuttingDown = false;

        var itemCount = monitoredItems.Count;
        var maxItemsPerSubscription = _configuration.MaxItemsPerSubscription;
        for (var i = 0; i < itemCount; i += maxItemsPerSubscription)
        {
            var subscription = subscriptionManager.Add(this, new OptionsMonitor<SubscriptionOptions>(new SubscriptionOptions
            {
                PublishingEnabled = true,
                PublishingInterval = TimeSpan.FromMilliseconds(_configuration.DefaultPublishingInterval),
                MinLifetimeInterval = TimeSpan.FromSeconds(60),
                KeepAliveCount = _configuration.SubscriptionKeepAliveCount,
                LifetimeCount = _configuration.SubscriptionLifetimeCount,
                Priority = _configuration.SubscriptionPriority,
                MaxNotificationsPerPublish = _configuration.SubscriptionMaxNotificationsPerPublish
            }));

            try
            {
                var batchEnd = Math.Min(i + maxItemsPerSubscription, itemCount);
                for (var j = i; j < batchEnd; j++)
                {
                    var request = monitoredItems[j];
                    var name = Interlocked.Increment(ref _lastItemName).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (subscription.MonitoredItems.TryAdd(name, new OptionsMonitor<MonitoredItemOptions>(request.Options), out var item) &&
                        item is not null)
                    {
                        TrackMonitoredItem(item, request);
                    }
                    else
                    {
                        _logger.LogError("Failed to add OPC UA monitored item for node {NodeId}.", request.NodeId);
                    }
                }

                await WaitUntilAppliedAsync(subscription, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DisposeSubscriptionsAsync([subscription]).ConfigureAwait(false);
                throw;
            }

            FilterOutFailedMonitoredItems(subscription);

            // Register properties with ReadAfterWriteManager now that we know revised sampling intervals
            RegisterPropertiesWithReadAfterWriteManager(subscription);

            // Add to collection AFTER initialization (temporal separation - health monitor never sees partial state)
            TryAddSubscription(subscription);
        }
    }

    internal void TrackMonitoredItem(IMonitoredItem item, MonitoredItemRequest request)
    {
        SetMonitoredItem(item.ClientHandle, new MonitoredItemState(request.Property, request.NodeId));
    }

    /// <summary>
    /// Waits until the engine created the subscription and attempted every monitored item once, which
    /// it does asynchronously. Items that failed carry their error afterwards.
    /// </summary>
    private async Task WaitUntilAppliedAsync(ISubscription subscription, CancellationToken cancellationToken)
    {
        var tracking = _tracking.GetValue(subscription, static _ => new SubscriptionTracking());

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_configuration.OperationTimeout);
        try
        {
            while (true)
            {
                // Taken before the check, so a state change between the check and the wait is not lost.
                var changed = tracking.WaitForChangeAsync();
                if (IsApplied(subscription))
                {
                    return;
                }

                await Task.WhenAny(changed, Task.Delay(AppliedCheckInterval, timeoutSource.Token)).ConfigureAwait(false);
                timeoutSource.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!subscription.Created)
            {
                throw new TimeoutException(
                    $"The OPC UA subscription was not created within {_configuration.OperationTimeout}.");
            }

            _logger.LogWarning(
                "Not all OPC UA monitored items were applied within {Timeout}. The remaining items are picked up by the health check.",
                _configuration.OperationTimeout);
        }
    }

    private static bool IsApplied(ISubscription subscription)
    {
        if (!subscription.Created)
        {
            return false;
        }

        foreach (var item in subscription.MonitoredItems.Items)
        {
            if (!item.Created && !ServiceResult.IsBad(item.Error))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public ValueTask OnDataChangeNotificationAsync(
        ISubscription subscription,
        uint sequenceNumber,
        DateTime publishTime,
        ReadOnlyMemory<DataValueChange> notification,
        PublishState publishStateMask,
        IReadOnlyList<string> stringTable)
    {
        OnDataChange(subscription, sequenceNumber, notification.Span);
        return default;
    }

    /// <inheritdoc />
    public ValueTask OnEventDataNotificationAsync(
        ISubscription subscription,
        uint sequenceNumber,
        DateTime publishTime,
        ReadOnlyMemory<EventNotification> notification,
        PublishState publishStateMask,
        IReadOnlyList<string> stringTable) => default;

    /// <inheritdoc />
    public ValueTask OnKeepAliveNotificationAsync(
        ISubscription subscription,
        uint sequenceNumber,
        DateTime publishTime,
        PublishState publishStateMask) => default;

    /// <inheritdoc />
    public ValueTask OnSubscriptionStateChangedAsync(
        ISubscription subscription,
        SubscriptionState state,
        PublishState publishStateMask,
        CancellationToken ct = default)
    {
        var tracking = _tracking.GetValue(subscription, static _ => new SubscriptionTracking());

        // The engine reports the aggregate over all partitions on every state change.
        tracking.PublishingStopped = (publishStateMask & PublishState.Stopped) != 0;

        if (state is SubscriptionState.Created or SubscriptionState.Deleted)
        {
            // A subscription created again on the server numbers its messages from 1 again, so its
            // messages must not be ranked against those applied before.
            tracking.ResetApplyStates();
        }

        tracking.SignalChange();
        return default;
    }

    internal void OnDataChange(ISubscription subscription, uint sequenceNumber, ReadOnlySpan<DataValueChange> notification)
    {
        if (_shuttingDown)
        {
            return;
        }

        var monitoredItemsCount = notification.Length;
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
                ref readonly var change = ref notification[i];
                if (_monitoredItems.TryGetValue(change.ClientHandle, out var monitoredItem))
                {
                    changes.Add(new NotificationUpdate(
                        monitoredItem,
                        _configuration.ValueConverter.ConvertToPropertyValue(
                            change.Value.WrappedValue.ToClrValue(), monitoredItem.Property),
                        change.Value.SourceTimestamp.ToDateTimeOffset()));
                }
            }
        }
        catch (Exception error)
        {
            // Return pooled list on exception to prevent pool exhaustion
            changes.Clear();
            ChangesPool.Return(changes);
            ReportErrorIfRunning(error);
            throw;
        }

        if (changes.Count > 0)
        {
            _source.IncomingThroughput.Add(changes.Count);

            // One notification message comes from one server-side partition, and sequence numbers
            // are per partition.
            var applyState = _tracking
                .GetValue(subscription, static _ => new SubscriptionTracking())
                .GetApplyState(notification[0].PartitionServerId);

            // Pool item returned inside callback. Safe because ApplyUpdate never throws:
            // It wraps callback execution in try-catch and only throws on catastrophic failures (lock/memory corruption).
            var state = (manager: this, source: _source, applyState, sequenceNumber, receivedTimestamp, changes, logger: _logger);
            _propertyWriter.Write(state, static s =>
            {
                // The ranking and the apply share one lock because a late message of a partition can be
                // delivered while a newer one is applied. Taken inside the writer's lock during
                // replay, so nothing may take the writer's lock while holding this one.
                lock (s.applyState.Lock)
                {
                    var stamp = s.applyState.GetStamp(s.sequenceNumber);
                    for (var i = 0; i < s.changes.Count; i++)
                    {
                        var change = s.changes[i];
                        var monitoredItem = change.MonitoredItem;
                        if (stamp != 0)
                        {
                            var lastApplied = Interlocked.Read(ref monitoredItem.LastAppliedStamp);
                            if (SubscriptionApplyState.IsOlder(stamp, lastApplied))
                            {
                                if (s.logger.IsEnabled(LogLevel.Debug))
                                {
                                    s.logger.LogDebug(
                                        "Skipped a stale value for property {PropertyName}: notification message {SequenceNumber} is older than the applied message {LastAppliedSequenceNumber}.",
                                        monitoredItem.Property.Name, s.sequenceNumber, SubscriptionApplyState.GetSequenceNumber(lastApplied));
                                }

                                continue;
                            }
                        }

                        try
                        {
                            monitoredItem.Property.SetValueFromSource(s.source, change.Timestamp, s.receivedTimestamp, change.Value);

                            // Only once the value landed: a rejected value must not outrank a late message
                            // that is still newer than the shown value.
                            if (stamp != 0)
                            {
                                Interlocked.Exchange(ref monitoredItem.LastAppliedStamp, stamp);
                            }
                        }
                        catch (Exception e)
                        {
                            // A change observer can throw after the value was stored, which still counts as applied.
                            if (stamp != 0 && Equals(monitoredItem.Property.GetValue(), change.Value))
                            {
                                Interlocked.Exchange(ref monitoredItem.LastAppliedStamp, stamp);
                            }

                            s.manager.ReportErrorIfRunning(e);
                            s.logger.LogError(e, "Failed to apply change for property {PropertyName}.", monitoredItem.Property.Name);
                        }
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

    private void ReportErrorIfRunning(Exception error, CancellationToken cancellationToken = default)
    {
        if (!_shuttingDown && !cancellationToken.IsCancellationRequested)
        {
            _reportError(error);
        }
    }

    /// <summary>
    /// Sorts the monitored items the server rejected: transient failures stay in the subscription for
    /// the health monitor, nodes that do not support subscriptions move to polling and permanent
    /// failures are removed. Items the engine has not attempted yet are left alone.
    /// </summary>
    internal void FilterOutFailedMonitoredItems(ISubscription subscription)
    {
        var removed = 0;
        var polled = 0;
        var keptForRetry = 0;

        var pollingEnabled = _configuration.EnablePollingFallback && _pollingManager != null;

        foreach (var monitoredItem in subscription.MonitoredItems.Items)
        {
            var statusCode = monitoredItem.Error?.StatusCode ?? StatusCodes.Good;
            if (!StatusCode.IsBad(statusCode))
            {
                continue;
            }

            switch (ClassifyFailedItem(statusCode, pollingEnabled))
            {
                case FailedMonitoredItemDisposition.KeepForRetry:
                    // Keep it in the subscription so the health monitor heals it; removing it here
                    // silently orphaned transiently-failed items.
                    keptForRetry++;
                    _logger.LogWarning("OPC UA monitored item {ClientHandle} failed transiently ({Status}); keeping it for the health monitor to retry.",
                        monitoredItem.ClientHandle, statusCode);
                    break;

                case FailedMonitoredItemDisposition.FallbackToPolling:
                    _logger.LogWarning("Monitored item {ClientHandle} does not support subscriptions ({Status}), falling back to polling",
                        monitoredItem.ClientHandle, statusCode);
                    RemoveAndFallBackToPolling(subscription, monitoredItem.ClientHandle, fallBackToPolling: true);
                    removed++;
                    polled++;
                    break;

                case FailedMonitoredItemDisposition.Drop:
                    _logger.LogError("OPC UA monitored item creation failed permanently (Handle={Handle}): {Status}",
                        monitoredItem.ClientHandle, statusCode);
                    RemoveAndFallBackToPolling(subscription, monitoredItem.ClientHandle, fallBackToPolling: false);
                    removed++;
                    break;
            }
        }

        if (removed > 0 || keptForRetry > 0)
        {
            _logger.LogWarning(
                "Removed {Removed} failed monitored items ({Polled} switched to polling), kept {Kept} for the health monitor to retry.",
                removed, polled, keptForRetry);
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

        return OpcUaStatusCodeClassifier.IsTransientError(statusCode)
            ? FailedMonitoredItemDisposition.KeepForRetry
            : FailedMonitoredItemDisposition.Drop;
    }

    /// <summary>
    /// Whether a retryable item that keeps failing should be escalated to polling (its retry bound
    /// was exceeded) rather than kept for the health monitor to retry.
    /// </summary>
    internal static bool ShouldEscalateToPolling(int consecutiveFailures, int maxAttempts)
    {
        return consecutiveFailures >= maxAttempts;
    }

    /// <summary>
    /// Removes the item from the subscription, which the engine applies to the server by itself, and
    /// optionally hands it to the polling manager.
    /// </summary>
    internal void RemoveAndFallBackToPolling(ISubscription subscription, uint clientHandle, bool fallBackToPolling)
    {
        subscription.MonitoredItems.TryRemove(clientHandle);

        if (TryRemoveMonitoredItem(clientHandle, out var state) && fallBackToPolling)
        {
            _pollingManager?.AddItem(state.NodeId, state.Property);
        }
    }

    /// <summary>
    /// Escalates a retryable item that keeps failing past <see cref="MaxHealAttemptsBeforeEscalation"/>
    /// to polling instead of retrying forever. Runs only when polling is enabled; recreating the
    /// subscriptions clears the polling set and re-attempts every item, so escalation is not permanent.
    /// </summary>
    public void EscalatePersistentlyFailedItems()
    {
        if (!_configuration.EnablePollingFallback || _pollingManager is null)
        {
            return;
        }

        foreach (var subscription in _subscriptions.Keys)
        {
            List<uint>? toEscalate = null;

            foreach (var monitoredItem in subscription.MonitoredItems.Items)
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
                    // A runtime transition to permanent-bad is left in the subscription until the next
                    // recreate, but drop any heal counter so the map only tracks currently-retryable failing items.
                    if (!_healAttempts.IsEmpty)
                    {
                        _healAttempts.TryRemove(monitoredItem.ClientHandle, out _);
                    }
                    continue;
                }

                var attempts = _healAttempts.AddOrUpdate(monitoredItem.ClientHandle, 1, static (_, current) => current + 1);

                if (ShouldEscalateToPolling(attempts, MaxHealAttemptsBeforeEscalation))
                {
                    (toEscalate ??= []).Add(monitoredItem.ClientHandle);
                }
            }

            if (toEscalate is not { Count: > 0 })
            {
                continue;
            }

            foreach (var clientHandle in toEscalate)
            {
                _healAttempts.TryRemove(clientHandle, out _);
                RemoveAndFallBackToPolling(subscription, clientHandle, fallBackToPolling: true);
            }

            _logger.LogWarning(
                "Escalated {Count} persistently-failing monitored items to polling after {Max} retries.",
                toEscalate.Count, MaxHealAttemptsBeforeEscalation);
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
            if (kvp.Value.Property.Reference.Subject == subject)
            {
                TryRemoveMonitoredItem(kvp.Key, out _);
                _healAttempts.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Registers all successfully created monitored items with ReadAfterWriteManager.
    /// Called once the items were applied, when the revised sampling intervals are known.
    /// </summary>
    private void RegisterPropertiesWithReadAfterWriteManager(ISubscription subscription)
    {
        if (_readAfterWriteManager is null)
        {
            return;
        }

        foreach (var item in subscription.MonitoredItems.Items)
        {
            if (item.Created && _monitoredItems.TryGetValue(item.ClientHandle, out var state))
            {
                var requestedInterval = GetRequestedSamplingInterval(state.Property);
                _readAfterWriteManager.RegisterProperty(state.NodeId, state.Property, requestedInterval, item.CurrentSamplingInterval);
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

    private bool TryAddSubscription(ISubscription subscription)
    {
        lock (_trackingMutationLock)
        {
            if (!_subscriptions.TryAdd(subscription, 0))
            {
                return false;
            }

            Volatile.Write(ref _subscriptionCount, _subscriptionCount + 1);
            return true;
        }
    }

    private void SetMonitoredItem(uint clientHandle, MonitoredItemState monitoredItem)
    {
        lock (_trackingMutationLock)
        {
            if (_monitoredItems.TryAdd(clientHandle, monitoredItem))
            {
                Volatile.Write(ref _monitoredItemCount, _monitoredItemCount + 1);
            }
            else
            {
                _monitoredItems[clientHandle] = monitoredItem;
            }
        }
    }

    private bool TryRemoveMonitoredItem(uint clientHandle, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MonitoredItemState? monitoredItem)
    {
        lock (_trackingMutationLock)
        {
            if (_monitoredItems.TryRemove(clientHandle, out monitoredItem))
            {
                Volatile.Write(ref _monitoredItemCount, _monitoredItemCount - 1);
                return true;
            }

            return false;
        }
    }

    private void ClearTrackedCollections()
    {
        lock (_trackingMutationLock)
        {
            _subscriptions.Clear();
            _monitoredItems.Clear();
            Volatile.Write(ref _subscriptionCount, 0);
            Volatile.Write(ref _monitoredItemCount, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;

        var subscriptions = _subscriptions.Keys.ToArray();
        ClearTrackedCollections();

        await DisposeSubscriptionsAsync(subscriptions).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the subscriptions, which removes them from the session and deletes them on the server.
    /// </summary>
    private async Task DisposeSubscriptionsAsync(IReadOnlyList<ISubscription> subscriptions)
    {
        foreach (var subscription in subscriptions)
        {
            try
            {
                await subscription.DisposeAsync().AsTask()
                    .WaitAsync(_configuration.SessionDisposalTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove an OPC UA subscription.");
            }
        }
    }

    private readonly struct NotificationUpdate(MonitoredItemState monitoredItem, object? value, DateTimeOffset timestamp)
    {
        public readonly MonitoredItemState MonitoredItem = monitoredItem;
        public readonly object? Value = value;
        public readonly DateTimeOffset Timestamp = timestamp;
    }

    internal sealed class MonitoredItemState(RegisteredSubjectProperty property, NodeId nodeId)
    {
        public RegisteredSubjectProperty Property { get; } = property;

        public NodeId NodeId { get; } = nodeId;

        /// <summary>
        /// The stamp of the last message applied to this item, 0 before the first. Read and written with
        /// <see cref="Interlocked"/>.
        /// </summary>
        // Interlocked rather than the partition's lock alone: a callback ranked against an apply state
        // that was reset meanwhile writes it under its own lock.
        public ulong LastAppliedStamp;
    }

    /// <summary>
    /// What the manager tracks per subscription: whether publishing stopped, a signal for state
    /// changes and the apply state of each server-side partition.
    /// </summary>
    private sealed class SubscriptionTracking
    {
        private readonly ConcurrentDictionary<uint, SubscriptionApplyState> _applyStates = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool PublishingStopped;

        public SubscriptionApplyState GetApplyState(uint partitionServerId) =>
            _applyStates.GetOrAdd(partitionServerId, static _ => new SubscriptionApplyState());

        public void ResetApplyStates() => _applyStates.Clear();

        public Task WaitForChangeAsync() => Volatile.Read(ref _changed).Task;

        public void SignalChange() =>
            Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Ranks the notification messages of one server-side subscription between two creations. A stamp
    /// packs a 24-bit epoch that identifies the instance into the high bits and the message's sequence
    /// number, extended past the 32-bit wrap, into the low 40 bits, which last about 2^40 messages.
    /// </summary>
    private sealed class SubscriptionApplyState
    {
        private const int SequenceBits = 40;
        private const ulong SequenceMask = (1UL << SequenceBits) - 1;

        private static int _lastEpoch;

        private readonly ulong _epoch = ((ulong)(uint)Interlocked.Increment(ref _lastEpoch) & 0xFF_FFFF) << SequenceBits;
        private ulong _highestSequenceNumber;

        public Lock Lock { get; } = new();

        /// <summary>
        /// Whether <paramref name="stamp"/> belongs to an older message of the same instance than
        /// <paramref name="lastApplied"/>. A stamp of another instance ranks against nothing.
        /// </summary>
        public static bool IsOlder(ulong stamp, ulong lastApplied) =>
            (stamp & ~SequenceMask) == (lastApplied & ~SequenceMask) && stamp < lastApplied;

        public static uint GetSequenceNumber(ulong stamp) => (uint)(stamp & SequenceMask);

        /// <summary>
        /// Returns the stamp for a message, or 0 for an unnumbered one. Must be called under <see cref="Lock"/>.
        /// </summary>
        public ulong GetStamp(uint sequenceNumber)
        {
            if (sequenceNumber == 0)
            {
                return 0;
            }

            // Extended relative to the highest number seen rather than per item, so an item that stays
            // unchanged for more than half the 32-bit range is not ranked against a wrapped number. Starts
            // one wrap in, so a message older than the first one seen still maps above 0.
            var extended = _highestSequenceNumber == 0
                ? (1UL << 32) + sequenceNumber
                : (ulong)((long)_highestSequenceNumber + (int)(sequenceNumber - (uint)_highestSequenceNumber));

            if (extended > _highestSequenceNumber)
            {
                _highestSequenceNumber = extended;
            }

            return _epoch | (extended & SequenceMask);
        }
    }
}
