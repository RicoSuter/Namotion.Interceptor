using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Events;

namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    private const int MaxGroupingTopologyReads = 4;
    private static readonly TimeSpan GroupingTopologyReadDelay = TimeSpan.FromMilliseconds(500);

    // Never disposed, see _configurationChanged in SonosSystem.Connection.cs.
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    // The monotonic timestamp from which the next scheduled poll is a full reconciliation. Guarded by _reconcileLock.
    // The first poll of a connection is one anyway.
    private long _fullReconcileDueAt = long.MinValue;

    // Whether a progress poll of the current poll failed. Written by the progress polls, which run while the poll that
    // started them holds _reconcileLock, and read by that poll once they completed.
    private bool _hasFailedProgressPoll;

    /// <summary>
    /// Returns the connection for a command to the player. Throws <see cref="InvalidOperationException"/> when the
    /// system or the player is not connected and <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    internal SonosConnection GetConnectionForCommand(string uuid)
    {
        lock (_connectionsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // A command would physically succeed while disconnected, but nothing would be polling or subscribed to
            // show its result.
            if (!IsConnected)
            {
                throw CreateNotConnectedException();
            }

            if (!Players.TryGetValue(uuid, out var player) || !player.IsConnected || _scope is not { } scope || !scope.Connections.TryGetValue(uuid, out var connection))
            {
                throw new InvalidOperationException($"The Sonos player {uuid} is not connected.");
            }

            return connection;
        }
    }

    /// <summary>
    /// Re-reads the players of the commanded player's group so a command's effect shows without events. Never
    /// throws, so the command's own result or exception is what its caller gets.
    /// </summary>
    internal async Task RefreshAfterCommandAsync(SonosPlayer player, CancellationToken cancellationToken)
    {
        CancellationTokenSource scopeCancellation;
        try
        {
            scopeCancellation = CreateScopeCancellation(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // Torn down or disposed since the command started; there is nothing left to read the state through.
            _logger.LogDebug(exception, "Skipped reading the Sonos state back after a command.");
            return;
        }

        using (scopeCancellation)
        {
            var groupKey = player.GroupKey;
            var pollStartedAt = NextOrder();
            var groupPlayers = Players.Values
                .Where(candidate => candidate.IsConnected && candidate.GroupKey == groupKey)
                .ToArray();

            try
            {
                await Task.WhenAll(groupPlayers.Select(candidate => PollPlayerAsync(candidate, pollStartedAt, scopeCancellation.Token)));
            }
            catch (OperationCanceledException exception)
            {
                _logger.LogDebug(exception, "Reading the Sonos state back after a command was cancelled.");
            }
        }
    }

    /// <summary>
    /// Runs grouping commands, then reads the topology back, also after a failure, since earlier commands may
    /// already have regrouped rooms. After success it first waits, about 2 seconds at most, until
    /// <paramref name="isApplied"/> holds. Only the commands' exception propagates; a failed read is logged.
    /// </summary>
    internal async Task RunGroupingCommandsAsync(Func<CancellationToken, Task> commands, Func<bool> isApplied, CancellationToken cancellationToken)
    {
        try
        {
            await commands(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await TryReconcileAfterGroupingAsync(cancellationToken);
            throw;
        }

        await TryWaitForGroupingAsync(isApplied, cancellationToken);
        await TryReconcileAfterGroupingAsync(cancellationToken);
    }

    // Sonos regroups after answering the command, so a read right after it can still show the old groups. Only the
    // topology is read again, a few times and a short delay apart, rather than optimistically applying the expected
    // groups, which a regroup that Sonos rejects or changes would leave wrong. A topology event also ends the wait.
    // These reads do not confirm missing players: a speaker can report part of the household while it regroups, and
    // two reads this close together would take the other rooms offline.
    private async Task TryWaitForGroupingAsync(Func<bool> isApplied, CancellationToken cancellationToken)
    {
        try
        {
            using var scopeCancellation = CreateScopeCancellation(cancellationToken);
            for (var read = 0; read < MaxGroupingTopologyReads && !isApplied(); read++)
            {
                if (read > 0)
                {
                    await Task.Delay(GroupingTopologyReadDelay, Clock, scopeCancellation.Token);
                }

                var pollStartedAt = NextOrder();
                var zoneGroupState = await GetSeedConnection().ReadZoneGroupStateAsync(scopeCancellation.Token);
                ApplyPolledTopology(zoneGroupState, pollStartedAt, confirmsMissingPlayers: false);
            }
        }
        catch (Exception exception)
        {
            // The reconciliation that follows reads the topology again and logs its own failure.
            _logger.LogDebug(exception, "Waiting for the Sonos topology to show a grouping command stopped early.");
        }
    }

    private async Task TryReconcileAfterGroupingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            // The commands' own result or exception is what the caller gets; the next poll reads the topology.
            _logger.LogDebug(exception, "Reading the Sonos topology after a grouping command was cancelled.");
        }
        catch (Exception exception)
        {
            // The next poll retries the read.
            _logger.LogWarning(exception, "Reading the Sonos topology after a grouping command failed.");
        }
    }

    /// <summary>
    /// Reads topology, state and favorites and updates the subscriptions. Throws
    /// <see cref="InvalidOperationException"/> when the connection is torn down before or while it runs.
    /// </summary>
    internal Task ReconcileAsync(CancellationToken cancellationToken) => RunPollAsync(forcesFullReconcile: true, cancellationToken);

    /// <summary>
    /// Runs a scheduled poll: a full reconciliation like <see cref="ReconcileAsync"/> once
    /// <see cref="FullReconcileInterval"/> has passed since the last one. Otherwise it reads the topology, fully reads
    /// every player whose event subscriptions are not live, of the others only what events do not deliver, and the
    /// favorites only while the topology subscription is not live. Throws like <see cref="ReconcileAsync"/>.
    /// </summary>
    internal Task PollAsync(CancellationToken cancellationToken) => RunPollAsync(forcesFullReconcile: false, cancellationToken);

    private async Task RunPollAsync(bool forcesFullReconcile, CancellationToken cancellationToken)
    {
        using var scopeCancellation = CreateScopeCancellation(cancellationToken);
        try
        {
            await PollCoreAsync(forcesFullReconcile, scopeCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && scopeCancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(DisconnectedMessage, exception);
        }
    }

    private async Task PollCoreAsync(bool forcesFullReconcile, CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var startedAt = Clock.GetTimestamp();
            var isFullReconcile = forcesFullReconcile || startedAt >= _fullReconcileDueAt;
            var repairsLostEvents = _fullReconcileDueAt == long.MinValue;
            _hasFailedProgressPoll = false;

            // Read at every poll: it is a single request, finds out that the seed stopped answering or rebooted, which
            // no event reports, and confirms players missing from a topology event.
            var seedConnection = GetSeedConnection();
            var topologyPollStartedAt = NextOrder();
            var zoneGroupState = await seedConnection.ReadZoneGroupStateAsync(cancellationToken);
            ApplyPolledTopology(zoneGroupState, topologyPollStartedAt);
            SyncConnections();

            // Checked after the topology, which may have found players or requested new subscriptions.
            var listener = GetEventListener() is { IsListening: true } runningListener ? runningListener : null;
            var eventListener = isFullReconcile ? null : listener;
            var now = Clock.GetTimestamp();
            var players = Players.Values.Where(player => player.IsInTopology).ToArray();
            await Task.WhenAll(StartPlayerPolls(players, eventListener, listener, repairsLostEvents, now, cancellationToken));
            if (_hasFailedProgressPoll)
            {
                // A speaker that did not answer may not have reached HomeBlaze with its events either.
                _fullReconcileDueAt = long.MinValue;
            }
            await Task.WhenAll(players
                .SelectMany(player => player.Satellites.Values)
                .Where(satellite => satellite.IsInTopology && satellite.NeedsStaticData)
                .Select(satellite => PollSatelliteAsync(satellite, cancellationToken)));

            if (eventListener is null || !IsSubscriptionLive(eventListener, TopologySubscriptionKey, now))
            {
                await RefreshFavoritesAsync(players, cancellationToken);
            }

            await EnsureSubscriptionsAsync(cancellationToken);
            if (isFullReconcile)
            {
                _fullReconcileDueAt = Clock.AddToTimestamp(startedAt, FullReconcileInterval);
            }

            LastUpdated = Clock.GetLocalNow();
        }
        catch
        {
            // Events sent while the speakers or the network did not answer are lost, so the next poll reads everything.
            _fullReconcileDueAt = long.MinValue;
            throw;
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    // Caller holds _reconcileLock. A player whose events are not live is read fully, a coordinator whose events are
    // live only for what they do not deliver, and a member with live events not at all. A null eventListener reads
    // every player fully. A full read repairs lost events when the player missed events or failed its last poll, or
    // when the last poll failed (repairsLostEvents); it gets the running listener to report them again.
    private List<Task> StartPlayerPolls(
        SonosPlayer[] players,
        SonosEventListener? eventListener,
        SonosEventListener? runningListener,
        bool repairsLostEvents,
        long now,
        CancellationToken cancellationToken)
    {
        var groups = Groups;
        var pollStartedAt = NextOrder();
        var polls = new List<Task>(players.Length);
        foreach (var player in players)
        {
            var isCoordinator = groups.ContainsKey(player.Uuid);
            var hasMissedEvents = false;
            if (eventListener is null || !AreEventsLive(player, isCoordinator, eventListener, now, out hasMissedEvents))
            {
                var repairs = repairsLostEvents || hasMissedEvents;
                polls.Add(PollPlayerAsync(player, pollStartedAt, cancellationToken, GetRepairListener(player, repairs, runningListener)));
            }
            else if (isCoordinator && player.GetProgressReads() is var (readsPosition, readsSleepTimer) && (readsPosition || readsSleepTimer))
            {
                polls.Add(PollProgressAsync(player, readsPosition, readsSleepTimer, pollStartedAt, cancellationToken));
            }
        }

        return polls;
    }

    private static SonosEventListener? GetRepairListener(SonosPlayer player, bool repairsLostEvents, SonosEventListener? runningListener) =>
        repairsLostEvents || player.HasFailedPolls ? runningListener : null;

    /// <summary>
    /// Returns whether events deliver the state of the player now, so a poll skips what they deliver: it is connected,
    /// its last poll succeeded, its static data is read, it needs no new subscriptions, its AVTransport,
    /// RenderingControl and, as a coordinator, GroupRenderingControl subscriptions are live, and none of them missed
    /// an event since the last call, which <paramref name="hasMissedEvents"/> reports. Caller holds _reconcileLock.
    /// </summary>
    private static bool AreEventsLive(SonosPlayer player, bool isCoordinator, SonosEventListener eventListener, long now, out bool hasMissedEvents)
    {
        // Taken from every subscription whatever the outcome: one full poll covers what each of them missed.
        hasMissedEvents = TakeMissedEvents(eventListener, player.AvTransportSubscriptionKey);
        hasMissedEvents |= TakeMissedEvents(eventListener, player.RenderingControlSubscriptionKey);
        if (isCoordinator)
        {
            hasMissedEvents |= TakeMissedEvents(eventListener, player.GroupRenderingControlSubscriptionKey);
        }

        return !hasMissedEvents &&
               player is { IsConnected: true, HasFailedPolls: false, NeedsStaticData: false, NewSubscriptionsRequests: 0 } &&
               AreSubscriptionsLive(player, isCoordinator, eventListener, now);
    }

    private static bool TakeMissedEvents(SonosEventListener eventListener, string key) =>
        eventListener.TryGetSubscription(key, out var subscription) && subscription.TakeMissedEvents();

    private static bool AreSubscriptionsLive(SonosPlayer player, bool isCoordinator, SonosEventListener eventListener, long now) =>
        IsSubscriptionLive(eventListener, player.AvTransportSubscriptionKey, now) &&
        IsSubscriptionLive(eventListener, player.RenderingControlSubscriptionKey, now) &&
        (!isCoordinator || IsSubscriptionLive(eventListener, player.GroupRenderingControlSubscriptionKey, now));

    /// <summary>
    /// Reports the device reachable unless teardown released the connection while the poll was in flight.
    /// </summary>
    private void ReportPollSucceededIfCurrent(SonosDevice device, SonosConnection connection)
    {
        // Teardown detaches the scope under the same lock before it marks devices offline, so a late success cannot
        // undo that. This and ReportPollFailedIfCurrent are the only subject writes made under
        // _connectionsLock; they take no subject state lock. The SonosSystem remarks state what that asks of change
        // subscribers.
        lock (_connectionsLock)
        {
            if (ReferenceEquals(_scope?.Connections.GetValueOrDefault(device.Uuid), connection))
            {
                device.ReportPollSucceeded();
            }
        }
    }

    /// <summary>
    /// Records a failed poll unless teardown released the connection while the poll was in flight.
    /// </summary>
    /// <returns>
    /// Whether the failure made the device unreachable or changed its message, so the caller logs it at Warning once;
    /// false for the first failure in a row, a repeated or a stale one.
    /// </returns>
    private bool ReportPollFailedIfCurrent(SonosDevice device, SonosConnection? connection, string message)
    {
        // A poll without a connection failed before sending anything, so it cannot be stale.
        lock (_connectionsLock)
        {
            return (connection is null || ReferenceEquals(_scope?.Connections.GetValueOrDefault(device.Uuid), connection)) &&
                   device.ReportPollFailed(message);
        }
    }

    /// <param name="player">The player to poll.</param>
    /// <param name="pollStartedAt">When the poll started, from <see cref="NextOrder"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="missedEventsListener">
    /// The listener whose subscriptions of the player may have missed events the poll repairs, or null. When an event
    /// replaced part of the poll or a read newly answered with a fault, a value only the missed event changed may
    /// still be stale, so the missed events are reported again for the next poll.
    /// </param>
    private Task PollPlayerAsync(SonosPlayer player, long pollStartedAt, CancellationToken cancellationToken, SonosEventListener? missedEventsListener = null) =>
        PollDeviceAsync(
            player,
            async (connection, newFaults, token) =>
            {
                if (player.NeedsStaticData)
                {
                    await ReadStaticDataAsync(player, connection, newFaults, token);
                }

                var reading = await connection.ReadPlayerAsync(player.IsHomeTheater, newFaults, token);
                var group = Groups.GetValueOrDefault(player.Uuid);
                var groupReading = group is null ? null : await connection.ReadGroupAsync(newFaults, token);

                // Applied outside _connectionsLock: the apply takes the subjects' state locks and fires change
                // notifications, whose subscribers may issue commands that take _connectionsLock. Stale values on a
                // device that teardown already marked offline are harmless.
                var isComplete = player.ApplyPoll(reading, pollStartedAt);
                if (groupReading is not null)
                {
                    isComplete &= group!.ApplyGroupRenderingControlPoll(groupReading, pollStartedAt);
                }

                if ((!isComplete || newFaults.Count > 0) &&
                    missedEventsListener?.TryGetSubscription(player.AvTransportSubscriptionKey, out var subscription) == true)
                {
                    subscription.ReportMissedEvents();
                }
            },
            "No connection to the player.",
            "Polling the Sonos player in {Room} failed.",
            player.RoomName,
            cancellationToken);

    private async Task PollProgressAsync(SonosPlayer player, bool readsPosition, bool readsSleepTimer, long pollStartedAt, CancellationToken cancellationToken)
    {
        await PollDeviceAsync(
            player,
            async (connection, newFaults, token) =>
            {
                var reading = await connection.ReadProgressAsync(readsPosition, readsSleepTimer, newFaults, token);
                player.ApplyProgressPoll(reading, pollStartedAt);
            },
            "No connection to the player.",
            "Polling the Sonos player in {Room} failed.",
            player.RoomName,
            cancellationToken);

        if (player.HasFailedPolls)
        {
            _hasFailedProgressPoll = true;
        }
    }

    private Task PollSatelliteAsync(SonosSatellite satellite, CancellationToken cancellationToken) =>
        PollDeviceAsync(
            satellite,
            (connection, newFaults, token) => ReadStaticDataAsync(satellite, connection, newFaults, token),
            "No connection to the satellite.",
            "Reading the Sonos satellite {Uuid} failed.",
            satellite.Uuid,
            cancellationToken);

    /// <summary>
    /// Reads a device through its connection and reports it reachable, or records a failed poll when the read throws,
    /// the second of which in a row makes it unreachable. A read the speaker answers with a UPnP fault keeps its
    /// previous values and counts as answered; only a transport failure throws.
    /// </summary>
    private async Task PollDeviceAsync(
        SonosDevice device,
        Func<SonosConnection, List<SonosReadFault>, CancellationToken, Task> read,
        string noConnectionMessage,
        string failureMessage,
        object? failureArgument,
        CancellationToken cancellationToken)
    {
        SonosConnection? connection = null;
        try
        {
            connection = FindConnection(device.Uuid)
                ?? throw new InvalidOperationException(noConnectionMessage);

            var newFaults = new List<SonosReadFault>();
            await read(connection, newFaults, cancellationToken);
            LogNewReadFaults(device, newFaults);
            ReportPollSucceededIfCurrent(device, connection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Logged at Warning once, when the device becomes unreachable, and at Debug otherwise, so neither a single
            // lost poll nor an offline device floods the log.
            LogFailure(ReportPollFailedIfCurrent(device, connection, exception.Message), exception, failureMessage, failureArgument);

            // Outside _connectionsLock, like every apply that takes a subject's state lock.
            if (!device.IsReachable)
            {
                device.ForgetStateOfOutage();
            }
        }
    }

    private static async Task ReadStaticDataAsync(SonosDevice device, SonosConnection connection, List<SonosReadFault> newFaults, CancellationToken cancellationToken)
    {
        var description = await connection.ReadDescriptionAsync(cancellationToken);
        if (await connection.ReadZoneInfoAsync(newFaults, cancellationToken) is { } zoneInfo)
        {
            device.ApplyStaticData(description, zoneInfo.SerialNumber, zoneInfo.MacAddress, zoneInfo.HardwareVersion, zoneInfo.DisplayVersion);
        }
        else
        {
            // The static data stays incomplete, so the next poll reads the zone info again.
            device.ApplyDescription(description);
        }
    }

    private void LogNewReadFaults(SonosDevice device, List<SonosReadFault> newFaults)
    {
        foreach (var fault in newFaults)
        {
            _logger.LogDebug(fault.Exception, "{Device} answered {Action} with a fault; the values it reports keep their last state.", device.Title, fault.Action);
        }
    }

    // Read through a player, not the seed: the seed may be a satellite, and satellites answer the favorites Browse
    // with HTTP 500.
    private async Task RefreshFavoritesAsync(SonosPlayer[] players, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        var candidates = new List<(SonosPlayer Player, SonosConnection Connection)>(players.Length);
        foreach (var player in players.Where(player => player.IsConnected).OrderBy(player => player.IsGroupCoordinator ? 0 : 1))
        {
            if (FindConnection(player.Uuid) is { } connection)
            {
                candidates.Add((player, connection));
            }
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var (player, connection) = candidates[index];
            try
            {
                SetFavorites(await connection.ReadFavoritesAsync(cancellationToken));
                _failures.ReportSuccess(FavoritesFailureKey);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failure = exception;
                if (index < candidates.Count - 1)
                {
                    _logger.LogDebug(exception, "Reading the Sonos favorites from {Room} failed; trying the next player.", player.RoomName);
                }
            }
        }

        failure ??= new InvalidOperationException("No connected Sonos player to read the favorites from.");
        LogFailure(_failures.ReportFailure(FavoritesFailureKey), failure, "Reading the Sonos favorites failed.");
    }
}
