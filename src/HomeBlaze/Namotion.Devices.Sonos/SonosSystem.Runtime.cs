using System.Net;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Events;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    private const int MaxConsecutiveReconcileFailures = 3;
    private const string AvTransportService = "AVTransport";
    private const string RenderingControlService = "RenderingControl";
    private const string GroupRenderingControlService = "GroupRenderingControl";
    private const string TopologySubscriptionKey = "seed/ZoneGroupTopology";
    private const string DisconnectedMessage = "The Sonos system is disconnected.";

    // Keys of _failures; subscription keys, which contain a slash, share it.
    private const string ConnectionFailureKey = "Connection";
    private const string SeedHostFailureKey = "SeedHost";
    private const string FavoritesFailureKey = "Favorites";
    private const string EventDeliveryFailureKey = "EventDelivery";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SeedProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumLoopWait = TimeSpan.FromSeconds(1);

    // Never disposed: neither exposes a wait handle, so there is nothing to release, and disposing them while the
    // loop is still unwinding from a Dispose without StopAsync made its last waits throw or hang.
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly Lock _connectionsLock = new();
    private readonly Dictionary<string, SonosConnection> _connections = new(StringComparer.Ordinal);

    // Guarded by _connectionsLock and replaced for every connection attempt. The scope owns the HttpClient:
    // SonosConnection and SonosEventListener only borrow it.
    private HttpClient? _httpClient;
    private SonosEventListener? _eventListener;
    private SonosConnection? _seedConnection;
    private bool _disposed;

    // Guarded by _connectionsLock and replaced with the scope. Teardown cancels it first, so a reconciliation or
    // command refresh running on a caller's token stops instead of writing state after the teardown.
    private CancellationTokenSource? _scopeCancellation;

    // The monotonic timestamp of the earliest subscription renewal, TimeProviderExtensions.Never for none. Written
    // under _reconcileLock, which also guards the subscriptions' RenewAt, and read by the connection loop while it
    // holds no lock.
    private long _nextRenewalAt = TimeProviderExtensions.Never;

    // The connection loop's current sleep. Cancelling it wakes the loop to reschedule when a reconciliation outside
    // the loop, from a command, schedules an earlier renewal. Guarded by _loopWakeLock.
    private readonly Lock _loopWakeLock = new();
    private CancellationTokenSource? _loopWake;

    // Serializes recomputing AreEventsActive: the first event of a subscription reports from the listener's thread
    // while a reconciliation recomputes, and a stale result written last would stick until the next pass.
    private readonly Lock _eventsActiveLock = new();

    // A persistent failure is logged at Warning once and at Debug while it lasts, see LogFailure.
    private readonly FailureTracker _failures = new();

    private TimeSpan EffectivePollingInterval => SonosValues.GetEffectiveInterval(PollingInterval, DefaultPollingInterval, MinimumInterval);

    private TimeSpan EffectiveRetryInterval => SonosValues.GetEffectiveInterval(RetryInterval, DefaultRetryInterval, MinimumInterval);

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_configurationChanged.CurrentCount == 0)
        {
            try
            {
                _configurationChanged.Release();
            }
            catch (SemaphoreFullException)
            {
                // A concurrent call signalled first; one pending restart covers both.
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var reconnectImmediately = false;
            Status = ServiceStatus.Starting;
            StatusMessage = null;

            try
            {
                OpenConnectionScope();
                var seedUri = await FindSeedAsync(stoppingToken)
                    ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(SeedHost)
                        ? "No Sonos speaker found. Set SeedHost when multicast discovery is blocked, for example under Docker bridge networking."
                        : $"The SeedHost '{SeedHost}' did not answer, and no other Sonos speaker was found.");

                StartEventListener(seedUri.Host);
                reconnectImmediately = await RunConnectedAsync(seedUri, stoppingToken);
            }
            catch (Exception exception) when (stoppingToken.IsCancellationRequested)
            {
                // Stopping (or disposed without stopping first) aborts whatever was in flight; that is no failure.
                _logger.LogDebug(exception, "The Sonos system connection ended while stopping.");
                break;
            }
            catch (Exception exception)
            {
                // A long outage retries every interval, so only a new failure is a Warning.
                LogFailure(_failures.ReportFailure(ConnectionFailureKey, exception.Message), exception, "Sonos system connection failed.");

                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
            }
            finally
            {
                await CloseConnectionScopeAsync();
            }

            if (!reconnectImmediately && !await WaitForRetryAsync(stoppingToken))
            {
                break;
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

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

            if (!Players.TryGetValue(uuid, out var player) || !player.IsConnected || !_connections.TryGetValue(uuid, out var connection))
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
            var coordinatorUuid = player.GroupCoordinatorUuid ?? player.Uuid;
            var pollStartedAt = Clock.GetUtcNow();
            var groupPlayers = Players.Values
                .Where(candidate => candidate.IsConnected && (candidate.GroupCoordinatorUuid ?? candidate.Uuid) == coordinatorUuid)
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
    /// already have regrouped rooms. Only the commands' exception propagates; a failed read is logged.
    /// </summary>
    internal async Task RunGroupingCommandsAsync(Func<CancellationToken, Task> commands, CancellationToken cancellationToken)
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

        await TryReconcileAfterGroupingAsync(cancellationToken);
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
    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        using var scopeCancellation = CreateScopeCancellation(cancellationToken);
        try
        {
            await ReconcileCoreAsync(scopeCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && scopeCancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(DisconnectedMessage, exception);
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var seedConnection = GetSeedConnection();
            var appliedTopologyEvents = GetAppliedTopologyEvents();
            var topology = await seedConnection.ReadTopologyAsync(cancellationToken);
            ApplyPolledTopology(topology, appliedTopologyEvents);
            SyncConnections();

            var pollStartedAt = Clock.GetUtcNow();
            var players = Players.Values.Where(player => player.IsInTopology).ToArray();
            await Task.WhenAll(players.Select(player => PollPlayerAsync(player, pollStartedAt, cancellationToken)));
            await Task.WhenAll(players
                .SelectMany(player => player.Satellites.Values)
                .Where(satellite => satellite.IsInTopology && satellite.NeedsStaticData)
                .Select(satellite => PollSatelliteAsync(satellite, cancellationToken)));

            await RefreshFavoritesAsync(players, cancellationToken);
            await EnsureSubscriptionsAsync(cancellationToken);
            LastUpdated = Clock.GetLocalNow();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void OpenConnectionScope()
    {
        ResetConnectionTopology();
        var httpClient = HttpClientFactory.CreateClient(nameof(SonosSystem));
        httpClient.Timeout = RequestTimeout;

        lock (_connectionsLock)
        {
            if (_disposed)
            {
                httpClient.Dispose();
                throw new ObjectDisposedException(nameof(SonosSystem));
            }

            _httpClient = httpClient;
            _scopeCancellation = new CancellationTokenSource();
            _eventListener = new SonosEventListener(httpClient, _logger, MinimumSubscriptionLifetime, Clock, UpdateAreEventsActive);
        }
    }

    /// <summary>
    /// Returns a source linked to the caller's token that the teardown of the current connection cancels. Throws
    /// <see cref="InvalidOperationException"/> when no connection is open and <see cref="ObjectDisposedException"/>
    /// after disposal.
    /// </summary>
    private CancellationTokenSource CreateScopeCancellation(CancellationToken cancellationToken)
    {
        lock (_connectionsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _scopeCancellation is { } scopeCancellation
                ? CancellationTokenSource.CreateLinkedTokenSource(scopeCancellation.Token, cancellationToken)
                : throw CreateNotConnectedException();
        }
    }

    private async Task CloseConnectionScopeAsync()
    {
        IsConnected = false;
        AreEventsActive = false;
        ActiveEventCallbackHost = null;

        // First, so a reconciliation or refresh running on a caller's token releases the lock promptly.
        CancellationTokenSource? scopeCancellation;
        lock (_connectionsLock)
        {
            scopeCancellation = _scopeCancellation;
        }

        if (scopeCancellation is not null)
        {
            await scopeCancellation.CancelAsync();
        }

        // The stopping token is already cancelled on shutdown, so teardown gets its own short budgets, one for the
        // lock and one for the unsubscribes, so a slow reconciliation cannot use up the time for unsubscribing. Holding
        // the reconcile lock keeps a command's reconciliation from subscribing again between unsubscribing and disposing.
        bool hasReconcileLock;
        using (var lockCancellation = new CancellationTokenSource(TeardownTimeout))
        {
            hasReconcileLock = await TryEnterReconcileLockAsync(lockCancellation.Token);
        }

        try
        {
            using var unsubscribeCancellation = new CancellationTokenSource(TeardownTimeout);
            await ReleaseConnectionScopeAsync(unsubscribeCancellation.Token);
        }
        finally
        {
            if (hasReconcileLock)
            {
                _reconcileLock.Release();
            }
        }
    }

    private async Task<bool> TryEnterReconcileLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _reconcileLock.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("A Sonos reconciliation did not finish within the teardown budget; tearing down anyway.");
            return false;
        }
    }

    private async Task ReleaseConnectionScopeAsync(CancellationToken cancellationToken)
    {
        // Again, after waiting for an in-flight reconciliation or for the budget to expire: one that was in flight
        // may have set them after the early reset.
        AreEventsActive = false;
        ActiveEventCallbackHost = null;
        _failures.ReportSuccess(EventDeliveryFailureKey);
        Interlocked.Exchange(ref _nextRenewalAt, TimeProviderExtensions.Never);

        SonosEventListener? eventListener;
        HttpClient? httpClient;
        CancellationTokenSource? scopeCancellation;
        List<SonosConnection> connections;
        lock (_connectionsLock)
        {
            eventListener = _eventListener;
            httpClient = _httpClient;
            scopeCancellation = _scopeCancellation;
            connections = [.. _connections.Values];
            if (_seedConnection is not null)
            {
                connections.Add(_seedConnection);
            }

            _connections.Clear();
            _seedConnection = null;
            _eventListener = null;
            _httpClient = null;
            _scopeCancellation = null;
        }

        // Cancelled already; sources linked to it before only unregister from it when they are disposed.
        scopeCancellation?.Dispose();

        if (eventListener is not null)
        {
            try
            {
                await eventListener.UnsubscribeAllAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unsubscribing the Sonos events failed; the speakers drop them once they expire.");
            }

            await eventListener.DisposeAsync();

            // After the handlers drained: a first event that arrived during teardown may have set it.
            UpdateAreEventsActive();
        }

        foreach (var connection in connections)
        {
            connection.Dispose();
        }

        httpClient?.Dispose();

        foreach (var player in Players.Values)
        {
            player.ReportPollFailed(DisconnectedMessage);
            foreach (var satellite in player.Satellites.Values)
            {
                satellite.ReportPollFailed(DisconnectedMessage);
            }
        }
    }

    private async Task<bool> WaitForRetryAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _configurationChanged.WaitAsync(EffectiveRetryInterval, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the configured SeedHost when it answers, otherwise a known speaker that answers, otherwise one found
    /// over SSDP. Throws <see cref="InvalidOperationException"/> for an invalid SeedHost.
    /// </summary>
    private async Task<Uri?> FindSeedAsync(CancellationToken cancellationToken)
    {
        Uri? seedHostUri = null;
        var seedHost = SeedHost;
        if (!string.IsNullOrWhiteSpace(seedHost))
        {
            try
            {
                seedHostUri = SonosDiscovery.CreateDeviceUri(seedHost);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"The SeedHost '{seedHost}' is invalid: {exception.Message}", exception);
            }

            // The full request timeout, not the probe budget for known speakers: the configured seed is preferred.
            if (await ProbeSeedAsync(seedHostUri, RequestTimeout, cancellationToken))
            {
                _failures.ReportSuccess(SeedHostFailureKey);
                return seedHostUri;
            }

            LogFailure(_failures.ReportFailure(SeedHostFailureKey), null,
                "The Sonos SeedHost {SeedHost} did not answer; trying the known speakers, then discovery.", seedHost);
        }

        // Players still in the last topology first: a missing one was more likely unplugged or replaced.
        var knownSeeds = Players.Values
            .Where(player => player.BaseUri is not null && player.BaseUri != seedHostUri)
            .OrderBy(player => player.IsInTopology ? 0 : 1)
            .Select(player => player.BaseUri!)
            .Distinct();

        foreach (var knownSeed in knownSeeds)
        {
            if (await ProbeSeedAsync(knownSeed, SeedProbeTimeout, cancellationToken))
            {
                return knownSeed;
            }
        }

        try
        {
            return await DiscoverSpeakerAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Searching for Sonos speakers over SSDP failed.");
            return null;
        }
    }

    private async Task<bool> ProbeSeedAsync(Uri seedUri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        HttpClient httpClient;
        lock (_connectionsLock)
        {
            httpClient = _httpClient ?? throw new InvalidOperationException("No Sonos connection scope is open.");
        }

        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(timeout);
        using var connection = new SonosConnection(seedUri, null, httpClient);
        try
        {
            await connection.ReadTopologyAsync(probeCancellation.Token);
            _logger.LogDebug("The Sonos speaker {Uri} answered and becomes the seed.", seedUri);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The Sonos speaker {Uri} did not answer.", seedUri);
            return false;
        }
    }

    private void StartEventListener(string seedHost)
    {
        var callbackHost = string.IsNullOrWhiteSpace(EventCallbackHost)
            ? SonosDiscovery.DetectLocalAddress(seedHost)
            : EventCallbackHost.Trim();

        if (callbackHost is null)
        {
            _logger.LogWarning("No local IPv4 address routes to the Sonos speaker {Host}; continuing with polling only.", seedHost);
            return;
        }

        try
        {
            _eventListener!.Start(callbackHost, EventPort, EventListenHost);
            ActiveEventCallbackHost = callbackHost;
        }
        catch (HttpListenerException exception)
        {
            // On Windows, listening on all interfaces needs a URL ACL or elevation.
            _logger.LogWarning(exception, "The Sonos event listener could not listen on port {Port}; continuing with polling only.", EventPort);
        }
    }

    private async Task<bool> RunConnectedAsync(Uri seedUri, CancellationToken stoppingToken)
    {
        SetSeed(seedUri);

        // The first reconciliation is what establishes the connection, so its failure ends this attempt.
        await ReconcileAsync(stoppingToken);
        MarkConnected();

        var consecutiveFailures = 0;
        var nextPollAt = Clock.GetTimestampAfter(EffectivePollingInterval);
        while (true)
        {
            bool isRenewalOnly;
            using (var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                lock (_loopWakeLock)
                {
                    _loopWake = wake;
                }

                try
                {
                    // Subscriptions live 30 minutes, which a long polling interval would let lapse, so the loop also
                    // wakes to renew them, without a full poll. Read after publishing the wake source: a renewal
                    // scheduled before is seen here, one scheduled after cancels the wait.
                    var nextRenewalAt = Interlocked.Read(ref _nextRenewalAt);
                    isRenewalOnly = nextRenewalAt < nextPollAt;
                    var wait = Clock.GetTimeUntil(isRenewalOnly ? nextRenewalAt : nextPollAt);
                    if (await _configurationChanged.WaitAsync(wait > MinimumLoopWait ? wait : MinimumLoopWait, wake.Token))
                    {
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Woken for an earlier renewal; the next iteration waits for it.
                    continue;
                }
                finally
                {
                    lock (_loopWakeLock)
                    {
                        _loopWake = null;
                    }
                }
            }

            if (isRenewalOnly)
            {
                await RenewSubscriptionsAsync(stoppingToken);
                continue;
            }

            try
            {
                await ReconcileAsync(stoppingToken);
                consecutiveFailures = 0;
                MarkConnected();
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveReconcileFailures)
                {
                    throw;
                }

                _logger.LogWarning(exception,
                    "Sonos reconciliation failed ({FailureCount} of {MaxFailureCount}); trying another speaker as seed.",
                    consecutiveFailures, MaxConsecutiveReconcileFailures);
                StatusMessage = $"Reconciliation failed ({consecutiveFailures} of {MaxConsecutiveReconcileFailures}): {exception.Message}";
                SetSeed(SelectNextSeed());
            }

            nextPollAt = Clock.GetTimestampAfter(EffectivePollingInterval);
        }
    }

    private void WakeLoopForRenewal()
    {
        lock (_loopWakeLock)
        {
            // Asynchronous, so the loop never continues on this thread; the loop disposes the source only after it
            // cleared the field under this lock.
            _ = _loopWake?.CancelAsync();
        }
    }

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

    private void MarkConnected()
    {
        _failures.ReportSuccess(ConnectionFailureKey);
        IsConnected = true;
        Status = ServiceStatus.Running;
        StatusMessage = null;
    }

    private void SetSeed(Uri seedUri)
    {
        lock (_connectionsLock)
        {
            if (_seedConnection?.BaseUri == seedUri)
            {
                return;
            }

            _seedConnection?.Dispose();
            _seedConnection = new SonosConnection(seedUri, null, _httpClient!);
        }
    }

    private SonosConnection GetSeedConnection()
    {
        lock (_connectionsLock)
        {
            return _seedConnection ?? throw new InvalidOperationException("No Sonos seed speaker is selected.");
        }
    }

    private Uri? GetSeedUri()
    {
        lock (_connectionsLock)
        {
            return _seedConnection?.BaseUri;
        }
    }

    private Uri SelectNextSeed()
    {
        var current = GetSeedUri();
        return Players.Values
            .Where(player => player.IsConnected && player.BaseUri is not null && player.BaseUri != current)
            .Select(player => player.BaseUri!)
            .FirstOrDefault() ?? current!;
    }

    private void SyncConnections()
    {
        lock (_connectionsLock)
        {
            if (_httpClient is null)
            {
                return;
            }

            foreach (var player in Players.Values)
            {
                SyncConnection(player);
                foreach (var satellite in player.Satellites.Values)
                {
                    SyncConnection(satellite);
                }
            }
        }
    }

    // Caller holds _connectionsLock.
    private void SyncConnection(SonosDevice device)
    {
        if (!device.IsInTopology || device.BaseUri is not { } baseUri)
        {
            return;
        }

        if (_connections.TryGetValue(device.Uuid, out var existing))
        {
            if (existing.BaseUri == baseUri)
            {
                return;
            }

            existing.Dispose();
        }

        _connections[device.Uuid] = new SonosConnection(baseUri, device.Uuid, _httpClient!, _logger);
        device.InvalidateStaticData();
    }

    private SonosConnection? FindConnection(string uuid)
    {
        lock (_connectionsLock)
        {
            return _connections.GetValueOrDefault(uuid);
        }
    }

    /// <summary>
    /// Reports the device reachable unless teardown released the connection while the poll was in flight.
    /// </summary>
    private void ReportPollSucceededIfCurrent(SonosDevice device, SonosConnection connection)
    {
        // Teardown clears the connections under the same lock before it marks devices offline, so a late success
        // cannot undo that. This and ReportPollFailedIfCurrent are the only subject writes made under
        // _connectionsLock; they take no subject state lock. The SonosSystem remarks state what that asks of change
        // subscribers.
        lock (_connectionsLock)
        {
            if (ReferenceEquals(_connections.GetValueOrDefault(device.Uuid), connection))
            {
                device.ReportPollSucceeded();
            }
        }
    }

    /// <summary>
    /// Records a failed poll unless teardown released the connection while the poll was in flight.
    /// </summary>
    /// <returns>Whether the failure is new, so the caller logs it at Warning once; false for a repeated or stale one.</returns>
    private bool ReportPollFailedIfCurrent(SonosDevice device, SonosConnection? connection, string message)
    {
        // A poll without a connection failed before sending anything, so it cannot be stale.
        lock (_connectionsLock)
        {
            return (connection is null || ReferenceEquals(_connections.GetValueOrDefault(device.Uuid), connection)) &&
                   device.ReportPollFailed(message);
        }
    }

    private SonosEventListener? GetEventListener()
    {
        lock (_connectionsLock)
        {
            return _eventListener;
        }
    }

    private async Task PollPlayerAsync(SonosPlayer player, DateTimeOffset pollStartedAt, CancellationToken cancellationToken)
    {
        SonosConnection? connection = null;
        try
        {
            connection = FindConnection(player.Uuid)
                ?? throw new InvalidOperationException("No connection to the player.");

            // A read the speaker answers with a UPnP fault keeps its previous values; only a transport failure, which
            // throws, makes the player unreachable.
            var newFaults = new List<SonosReadFault>();
            if (player.NeedsStaticData)
            {
                await ReadStaticDataAsync(player, connection, newFaults, cancellationToken);
            }

            var reading = await connection.ReadPlayerAsync(player.IsHomeTheater, newFaults, cancellationToken);
            var group = Groups.GetValueOrDefault(player.Uuid);
            var groupReading = group is null ? null : await connection.ReadGroupAsync(newFaults, cancellationToken);
            LogNewReadFaults(player, newFaults);

            // Applied outside _connectionsLock: the apply takes the subjects' state locks and fires change
            // notifications, whose subscribers may issue commands that take _connectionsLock. Stale values on a
            // device that teardown already marked offline are harmless.
            player.ApplyPoll(reading, pollStartedAt);
            if (groupReading is not null)
            {
                group!.ApplyGroupRenderingControlPoll(groupReading, pollStartedAt);
            }

            ReportPollSucceededIfCurrent(player, connection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Logged at Warning once per failure transition, so an offline player does not flood the log every poll.
            LogFailure(ReportPollFailedIfCurrent(player, connection, exception.Message), exception,
                "Polling the Sonos player in {Room} failed.", player.RoomName);
        }
    }

    private async Task PollSatelliteAsync(SonosSatellite satellite, CancellationToken cancellationToken)
    {
        SonosConnection? connection = null;
        try
        {
            connection = FindConnection(satellite.Uuid)
                ?? throw new InvalidOperationException("No connection to the satellite.");

            var newFaults = new List<SonosReadFault>();
            await ReadStaticDataAsync(satellite, connection, newFaults, cancellationToken);
            LogNewReadFaults(satellite, newFaults);
            ReportPollSucceededIfCurrent(satellite, connection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure(ReportPollFailedIfCurrent(satellite, connection, exception.Message), exception,
                "Reading the Sonos satellite {Uuid} failed.", satellite.Uuid);
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
        var candidates = players
            .Where(player => player.IsConnected)
            .OrderBy(player => player.IsGroupCoordinator ? 0 : 1)
            .Select(player => (Player: player, Connection: FindConnection(player.Uuid)))
            .Where(candidate => candidate.Connection is not null)
            .ToArray();

        for (var index = 0; index < candidates.Length; index++)
        {
            var (player, connection) = candidates[index];
            try
            {
                SetFavorites(await connection!.ReadFavoritesAsync(cancellationToken));
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
                if (index < candidates.Length - 1)
                {
                    _logger.LogDebug(exception, "Reading the Sonos favorites from {Room} failed; trying the next player.", player.RoomName);
                }
            }
        }

        failure ??= new InvalidOperationException("No connected Sonos player to read the favorites from.");
        LogFailure(_failures.ReportFailure(FavoritesFailureKey), failure, "Reading the Sonos favorites failed.");
    }

    private async Task EnsureSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var eventListener = GetEventListener();
        if (eventListener is null || !eventListener.IsListening)
        {
            // Never started, or its accept loop died (which it logs); polling keeps the state current.
            UpdateAreEventsActive();
            ActiveEventCallbackHost = null;
            Interlocked.Exchange(ref _nextRenewalAt, TimeProviderExtensions.Never);
            return;
        }

        var desired = GetDesiredSubscriptions();
        var now = Clock.GetTimestamp();
        try
        {
            // Concurrently: a speaker that left does not answer, and each request may take the whole timeout while
            // the reconcile lock blocks commands.
            var obsolete = eventListener.Subscriptions
                .Where(subscription => !desired.TryGetValue(subscription.Key, out var target) || target.EventUri != subscription.EventUri)
                .ToArray();
            await Task.WhenAll(obsolete.Select(subscription =>
                RunSubscriptionRequestAsync(() => eventListener.UnsubscribeAsync(subscription, cancellationToken), subscription.Key, cancellationToken)));

            foreach (var subscription in eventListener.Subscriptions)
            {
                if (subscription.Sid is not null && subscription.RenewAt <= now &&
                    !await RunSubscriptionRequestAsync(() => eventListener.RenewAsync(subscription, cancellationToken), subscription.Key, cancellationToken))
                {
                    // Keeps an unreachable speaker from being retried at every loop wake.
                    subscription.RenewAt = Clock.GetTimestampAfter(FailedRenewalRetryDelay);
                }
            }

            var active = eventListener.Subscriptions.Select(subscription => subscription.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, target) in desired)
            {
                if (target.CanSubscribe && !active.Contains(key))
                {
                    await RunSubscriptionRequestAsync(() => eventListener.SubscribeAsync(key, target.EventUri, target.Handler, cancellationToken), key, cancellationToken);
                }
            }

            UpdateAreEventsActive();
            ReportMissingInitialEvents(eventListener);
        }
        finally
        {
            // Also when cancelled: a subscription made before the cancellation must still be renewed.
            ScheduleRenewal(eventListener);
        }
    }

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

    private void LogFailure(bool isNew, Exception? exception, string message, params object?[] args) =>
        _logger.Log(isNew ? LogLevel.Warning : LogLevel.Debug, exception, message, args);

    private long GetInitialEventDeadline(SonosEventSubscription subscription) =>
        Clock.AddToTimestamp(subscription.SubscribedAt, InitialEventTimeout);

    // Caller holds _reconcileLock, which guards the subscriptions' RenewAt. The loop also wakes when a first event
    // is due, so a missing one is reported without waiting for the next poll.
    private void ScheduleRenewal(SonosEventListener eventListener)
    {
        var now = Clock.GetTimestamp();
        var nextRenewalAt = TimeProviderExtensions.Never;
        foreach (var subscription in eventListener.Subscriptions)
        {
            if (subscription.Sid is not null)
            {
                nextRenewalAt = Math.Min(nextRenewalAt, subscription.RenewAt);
                if (!subscription.HasReceivedEvent && GetInitialEventDeadline(subscription) is var deadline && deadline > now)
                {
                    nextRenewalAt = Math.Min(nextRenewalAt, deadline);
                }
            }
        }

        if (nextRenewalAt < Interlocked.Exchange(ref _nextRenewalAt, nextRenewalAt))
        {
            WakeLoopForRenewal();
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
    /// Returns the subscriptions to hold. One missed poll does not drop a player's subscriptions: they are kept while
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
            desired[$"{player.Uuid}/{AvTransportService}"] = new SubscriptionTarget(
                new Uri(baseUri, "/MediaRenderer/AVTransport/Event"), body => OnAvTransportEvent(player, body), canSubscribe);
            desired[$"{player.Uuid}/{RenderingControlService}"] = new SubscriptionTarget(
                new Uri(baseUri, "/MediaRenderer/RenderingControl/Event"), body => OnRenderingControlEvent(player, body), canSubscribe);

            if (groups.ContainsKey(player.Uuid))
            {
                var coordinatorUuid = player.Uuid;
                desired[$"{player.Uuid}/{GroupRenderingControlService}"] = new SubscriptionTarget(
                    new Uri(baseUri, "/MediaRenderer/GroupRenderingControl/Event"), body => OnGroupRenderingControlEvent(coordinatorUuid, body), canSubscribe);
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
        player.ApplyAvTransportEvent(UpnpEventParser.ParseAvTransport(body), Clock.GetUtcNow());

    private void OnRenderingControlEvent(SonosPlayer player, string body) =>
        player.ApplyRenderingControlEvent(UpnpEventParser.ParseRenderingControl(body), Clock.GetUtcNow());

    private void OnGroupRenderingControlEvent(string coordinatorUuid, string body)
    {
        if (Groups.TryGetValue(coordinatorUuid, out var group))
        {
            group.ApplyGroupRenderingControlEvent(UpnpEventParser.ParseGroupRenderingControl(body), Clock.GetUtcNow());
        }
    }

    // Connections follow at once, so a command after an IP change reaches the new address. New players are polled,
    // and then subscribed, at the next reconciliation.
    private void OnTopologyEvent(string body)
    {
        var zoneGroupState = UpnpEventParser.ParseZoneGroupState(body);
        if (!string.IsNullOrEmpty(zoneGroupState))
        {
            ApplyTopologyEvent(ZoneGroupStateParser.Parse(zoneGroupState));
            SyncConnections();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // Cancel first: base.Dispose() cancels the stopping token, so the loop cannot open another connection
        // after the flag below closes the door.
        base.Dispose();
        lock (_connectionsLock)
        {
            _disposed = true;
        }
    }
}
