using Namotion.Devices.Sonos.Events;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    internal const string AvTransportService = "AVTransport";
    internal const string RenderingControlService = "RenderingControl";
    internal const string GroupRenderingControlService = "GroupRenderingControl";
    private const string TopologySubscriptionKey = "seed/ZoneGroupTopology";

    // The monotonic timestamp of the loop's next subscription wake, for the earliest renewal or first-event deadline,
    // TimeProviderExtensions.Never for none. Written under _reconcileLock, which also guards the subscriptions'
    // RenewAt, ExpiresAt and HasFailedRenewal, and read by the connection loop while it holds no lock.
    private long _nextWakeAt = TimeProviderExtensions.Never;

    // Serializes recomputing AreEventsActive: the first event of a subscription reports from the listener's thread
    // while a reconciliation recomputes, and a stale result written last would stick until the next pass.
    private readonly Lock _eventsActiveLock = new();

    private async Task RenewSubscriptionsAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureSubscriptionsAsync(cancellationToken);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async Task EnsureSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var eventListener = GetEventListener();
        if (eventListener is null || !eventListener.IsListening)
        {
            // Never started, or its accept loop died (which it logs); polling keeps the state current.
            UpdateAreEventsActive();
            ActiveEventCallbackHost = null;
            Interlocked.Exchange(ref _nextWakeAt, TimeProviderExtensions.Never);
            return;
        }

        var desired = GetDesiredSubscriptions();
        var requests = GetNewSubscriptionsRequests(out var replaced);
        var now = Clock.GetTimestamp();
        try
        {
            // Each stage concurrently: a speaker that left does not answer, and each request may take the whole
            // timeout while the reconcile lock blocks commands. The stages stay in order, so a rejected renewal, which
            // forgets its subscription, and a replaced one are subscribed again in the last one.
            var obsolete = eventListener.Subscriptions
                .Where(subscription =>
                    !desired.TryGetValue(subscription.Key, out var target) ||
                    target.EventUri != subscription.EventUri ||
                    replaced?.Contains(subscription.Key) == true)
                .ToArray();
            await Task.WhenAll(obsolete.Select(subscription =>
                RunSubscriptionRequestAsync(() => eventListener.UnsubscribeAsync(subscription, cancellationToken), subscription.Key, cancellationToken)));

            var due = eventListener.Subscriptions
                .Where(subscription => subscription.Sid is not null && subscription.RenewAt <= now)
                .ToArray();
            await Task.WhenAll(due.Select(subscription => RenewAsync(eventListener, subscription, cancellationToken)));

            // Taken before the first SUBSCRIBE: each registers its key on the listener right away.
            var missing = desired
                .Where(pair => pair.Value.CanSubscribe && !eventListener.HasSubscription(pair.Key))
                .ToArray();
            await Task.WhenAll(missing.Select(pair =>
                RunSubscriptionRequestAsync(() => eventListener.SubscribeAsync(pair.Key, pair.Value.EventUri, pair.Value.Handler, cancellationToken), pair.Key, cancellationToken)));

            // A replacement that failed left no subscription, which the next upkeep subscribes again.
            if (requests is not null)
            {
                foreach (var (player, handled) in requests)
                {
                    player.CompleteNewSubscriptionsRequests(handled);
                }
            }

            UpdateAreEventsActive();
            ReportMissingInitialEvents(eventListener);
        }
        finally
        {
            // Also when cancelled: a subscription made before the cancellation must still be renewed.
            ScheduleWake(eventListener);
        }
    }

    private async Task RenewAsync(SonosEventListener eventListener, SonosEventSubscription subscription, CancellationToken cancellationToken)
    {
        if (!await RunSubscriptionRequestAsync(() => eventListener.RenewAsync(subscription, cancellationToken), subscription.Key, cancellationToken))
        {
            // Keeps an unreachable speaker from being retried at every loop wake.
            subscription.RenewAt = Clock.GetTimestampAfter(FailedRenewalRetryDelay);
            subscription.HasFailedRenewal = true;
        }
    }

    // Caller holds _reconcileLock. Returns the players with pending requests and how many each had, and the keys of
    // the subscriptions to replace for them; both null for none. The topology subscription is replaced with them,
    // since the seed may be the speaker that rebooted.
    private List<(SonosPlayer Player, int Handled)>? GetNewSubscriptionsRequests(out HashSet<string>? replaced)
    {
        List<(SonosPlayer Player, int Handled)>? requests = null;
        replaced = null;
        foreach (var player in Players.Values)
        {
            if (player.NewSubscriptionsRequests is var handled and not 0)
            {
                requests ??= [];
                requests.Add((player, handled));
                replaced ??= new HashSet<string>(StringComparer.Ordinal) { TopologySubscriptionKey };
                replaced.Add(player.AvTransportSubscriptionKey);
                replaced.Add(player.RenderingControlSubscriptionKey);
                replaced.Add(player.GroupRenderingControlSubscriptionKey);
            }
        }

        return requests;
    }

    /// <summary>
    /// Returns whether the subscription delivers events now: the speaker accepted it, an event arrived, its last
    /// renewal was answered and its lifetime has not ended. Caller holds _reconcileLock, which guards those values.
    /// </summary>
    private static bool IsSubscriptionLive(SonosEventListener eventListener, string key, long now) =>
        eventListener.TryGetSubscription(key, out var subscription) &&
        subscription is { Sid: not null, HasReceivedEvent: true, HasFailedRenewal: false } &&
        now < subscription.ExpiresAt;

    private void UpdateAreEventsActive()
    {
        lock (_eventsActiveLock)
        {
            var eventListener = GetEventListener();
            AreEventsActive = eventListener is { IsListening: true } &&
                              eventListener.Subscriptions.Any(subscription => subscription.HasReceivedEvent);
        }
    }

    // Caller holds _reconcileLock. Speakers send a full-state NOTIFY right after accepting a subscription, so a
    // missing one means they cannot reach the callback, which outbound SUBSCRIBE requests never notice.
    private void ReportMissingInitialEvents(SonosEventListener eventListener)
    {
        var now = Clock.GetTimestamp();
        var overdue = eventListener.Subscriptions.FirstOrDefault(subscription =>
            subscription.Sid is not null && !subscription.HasReceivedEvent && GetInitialEventDeadline(subscription) <= now);

        if (overdue is null)
        {
            _failures.ReportSuccess(EventDeliveryFailureKey);
        }
        else
        {
            LogFailure(_failures.ReportFailure(EventDeliveryFailureKey), null,
                "The Sonos speakers accepted the event subscriptions, but no event reached {CallbackUri} within {Timeout} ({Key}); polling keeps the state current. " +
                "Check that no firewall blocks the port, that Docker publishes it, and that EventCallbackHost is an address the speakers can reach.",
                eventListener.CallbackBaseUri, InitialEventTimeout, overdue.Key);
        }
    }

    private long GetInitialEventDeadline(SonosEventSubscription subscription) =>
        Clock.AddToTimestamp(subscription.SubscribedAt, InitialEventTimeout);

    // Caller holds _reconcileLock, which guards the subscriptions' RenewAt. The loop wakes for the earliest renewal
    // and also when a first event is due, so a missing one is reported without waiting for the next poll.
    private void ScheduleWake(SonosEventListener eventListener)
    {
        var now = Clock.GetTimestamp();
        var nextWakeAt = TimeProviderExtensions.Never;
        foreach (var subscription in eventListener.Subscriptions)
        {
            if (subscription.Sid is not null)
            {
                nextWakeAt = Math.Min(nextWakeAt, subscription.RenewAt);
                if (!subscription.HasReceivedEvent && GetInitialEventDeadline(subscription) is var deadline && deadline > now)
                {
                    nextWakeAt = Math.Min(nextWakeAt, deadline);
                }
            }
        }

        if (nextWakeAt < Interlocked.Exchange(ref _nextWakeAt, nextWakeAt))
        {
            WakeLoop();
        }
    }

    /// <returns>Whether the request completed without an exception.</returns>
    private async Task<bool> RunSubscriptionRequestAsync(Func<Task> request, string key, CancellationToken cancellationToken)
    {
        try
        {
            await request();
            _failures.ReportSuccess(key);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Includes the listener's OperationCanceledException for a subscription unsubscribed concurrently.
            LogFailure(_failures.ReportFailure(key), exception, "The Sonos event subscription {Key} failed; polling keeps its state current.", key);
            return false;
        }
    }

    /// <summary>
    /// Returns the subscriptions to hold. Missed polls do not drop a player's subscriptions: they are kept while
    /// it stays in the topology, and a renewal finds out whether the speaker still holds them. New ones are only made
    /// to connected players.
    /// </summary>
    private Dictionary<string, SubscriptionTarget> GetDesiredSubscriptions()
    {
        var desired = new Dictionary<string, SubscriptionTarget>(StringComparer.Ordinal);
        var groups = Groups;
        foreach (var player in Players.Values)
        {
            if (!player.IsInTopology || player.BaseUri is not { } baseUri)
            {
                continue;
            }

            var canSubscribe = player.IsConnected;
            void Add(string key, string service, Action<string> handler) =>
                desired[key] = new SubscriptionTarget(new Uri(baseUri, $"/MediaRenderer/{service}/Event"), handler, canSubscribe);

            Add(player.AvTransportSubscriptionKey, AvTransportService, body => OnAvTransportEvent(player, body));
            Add(player.RenderingControlSubscriptionKey, RenderingControlService, body => OnRenderingControlEvent(player, body));
            if (groups.ContainsKey(player.Uuid))
            {
                var coordinatorUuid = player.Uuid;
                Add(player.GroupRenderingControlSubscriptionKey, GroupRenderingControlService, body => OnGroupRenderingControlEvent(coordinatorUuid, body));
            }
        }

        if (GetSeedUri() is { } seedUri)
        {
            desired[TopologySubscriptionKey] = new SubscriptionTarget(new Uri(seedUri, "/ZoneGroupTopology/Event"), OnTopologyEvent, CanSubscribe: true);
        }

        return desired;
    }

    private sealed record SubscriptionTarget(Uri EventUri, Action<string> Handler, bool CanSubscribe);

    // The event handlers run on the listener's thread pool callbacks, concurrently with polling. The listener catches
    // and logs a parser's XmlException, and its disposal waits for running handlers, so they must not block on it.

    private void OnAvTransportEvent(SonosPlayer player, string body) =>
        player.ApplyAvTransportEvent(UpnpEventParser.ParseAvTransport(body), NextOrder());

    private void OnRenderingControlEvent(SonosPlayer player, string body) =>
        player.ApplyRenderingControlEvent(UpnpEventParser.ParseRenderingControl(body), NextOrder());

    private void OnGroupRenderingControlEvent(string coordinatorUuid, string body)
    {
        if (Groups.TryGetValue(coordinatorUuid, out var group))
        {
            group.ApplyGroupRenderingControlEvent(UpnpEventParser.ParseGroupRenderingControl(body), NextOrder());
        }
    }

    // Connections follow at once, so a command after an IP change reaches the new address. New players are polled,
    // and then subscribed, at the next reconciliation.
    private void OnTopologyEvent(string body)
    {
        var zoneGroupState = UpnpEventParser.ParseZoneGroupState(body);
        if (!string.IsNullOrEmpty(zoneGroupState))
        {
            ApplyTopologyEvent(zoneGroupState);
            SyncConnections();
        }
    }
}
