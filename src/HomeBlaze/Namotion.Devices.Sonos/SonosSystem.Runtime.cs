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
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SeedProbeTimeout = TimeSpan.FromSeconds(2);

    // Never disposed: neither exposes a wait handle, so there is nothing to release, and disposing them while the
    // loop is still unwinding from a Dispose without StopAsync made its last waits throw or hang.
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly Lock _connectionsLock = new();
    private readonly Dictionary<string, SonosConnection> _connections = new(StringComparer.Ordinal);

    // Guarded by _connectionsLock and replaced for every connection attempt. The scope owns the HttpClient:
    // SonosConnection and SonosEventListener only borrow it.
    private HttpClient? _httpClient;
    private SonosClientProvider? _clientProvider;
    private SonosEventListener? _eventListener;
    private SonosConnection? _seedConnection;
    private bool _disposed;

    private TimeSpan EffectivePollingInterval => PollingInterval > TimeSpan.Zero ? PollingInterval : DefaultPollingInterval;

    private TimeSpan EffectiveRetryInterval => RetryInterval > TimeSpan.Zero ? RetryInterval : DefaultRetryInterval;

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
                    ?? throw new InvalidOperationException(
                        "No Sonos speaker found. Set SeedHost when multicast discovery is blocked, for example under Docker bridge networking.");

                StartEventListener(seedUri.Host);
                reconnectImmediately = await RunConnectedAsync(seedUri, stoppingToken);
            }
            catch (Exception exception) when (stoppingToken.IsCancellationRequested &&
                                              exception is OperationCanceledException or ObjectDisposedException)
            {
                // Stopped, or disposed without stopping first.
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Sonos system connection failed.");
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
    /// Returns the connection for a command, refusing when the system or the player is not connected: a command
    /// would physically succeed, but nothing would be polling or subscribed to show its result.
    /// </summary>
    internal SonosConnection GetConnectionForCommand(string uuid)
    {
        lock (_connectionsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsConnected)
            {
                throw new InvalidOperationException(
                    "The Sonos system is not connected. " + (StatusMessage ?? "Waiting for the connection to be established."));
            }

            if (!Players.TryGetValue(uuid, out var player) || !player.IsConnected || !_connections.TryGetValue(uuid, out var connection))
            {
                throw new InvalidOperationException($"The Sonos player {uuid} is not connected.");
            }

            return connection;
        }
    }

    /// <summary>
    /// Re-reads the players of the commanded player's group so a command's effect shows without events.
    /// </summary>
    internal async Task RefreshAfterCommandAsync(SonosPlayer player, CancellationToken cancellationToken)
    {
        var coordinatorUuid = player.GroupCoordinatorUuid ?? player.Uuid;
        var pollStartedAt = TimeProvider.System.GetUtcNow();
        var groupPlayers = Players.Values
            .Where(candidate => candidate.IsConnected && (candidate.GroupCoordinatorUuid ?? candidate.Uuid) == coordinatorUuid)
            .ToArray();

        await Task.WhenAll(groupPlayers.Select(candidate => PollPlayerAsync(candidate, pollStartedAt, cancellationToken)));
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var seedConnection = GetSeedConnection();
            var topology = await seedConnection.ReadTopologyAsync(cancellationToken);
            ApplyTopology(topology);
            SyncConnections();

            var pollStartedAt = TimeProvider.System.GetUtcNow();
            var players = Players.Values.Where(player => player.IsInTopology).ToArray();
            await Task.WhenAll(players.Select(player => PollPlayerAsync(player, pollStartedAt, cancellationToken)));
            await Task.WhenAll(players
                .SelectMany(player => player.Satellites.Values)
                .Where(satellite => satellite.IsInTopology && satellite.NeedsStaticData)
                .Select(satellite => PollSatelliteAsync(satellite, cancellationToken)));

            await RefreshFavoritesAsync(seedConnection, cancellationToken);
            await EnsureSubscriptionsAsync(cancellationToken);
            LastUpdated = DateTimeOffset.Now;
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void OpenConnectionScope()
    {
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
            _clientProvider = new SonosClientProvider(httpClient);
            _eventListener = new SonosEventListener(httpClient, _logger);
        }
    }

    private async Task CloseConnectionScopeAsync()
    {
        IsConnected = false;
        AreEventsActive = false;
        ActiveEventCallbackHost = null;

        // The stopping token is already cancelled on shutdown, so teardown gets its own short budget. Holding the
        // reconcile lock keeps a command's reconciliation from subscribing again between unsubscribing and disposing.
        using var teardownCancellation = new CancellationTokenSource(TeardownTimeout);
        var hasReconcileLock = await TryEnterReconcileLockAsync(teardownCancellation.Token);
        try
        {
            await ReleaseConnectionScopeAsync(teardownCancellation.Token);
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
        SonosEventListener? eventListener;
        HttpClient? httpClient;
        List<SonosConnection> connections;
        lock (_connectionsLock)
        {
            eventListener = _eventListener;
            httpClient = _httpClient;
            connections = [.. _connections.Values];
            if (_seedConnection is not null)
            {
                connections.Add(_seedConnection);
            }

            _connections.Clear();
            _seedConnection = null;
            _eventListener = null;
            _clientProvider = null;
            _httpClient = null;
        }

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

    private async Task<Uri?> FindSeedAsync(CancellationToken cancellationToken)
    {
        var seedHost = SeedHost;
        if (!string.IsNullOrWhiteSpace(seedHost))
        {
            try
            {
                return SonosDiscovery.CreateDeviceUri(seedHost);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"The SeedHost '{seedHost}' is invalid: {exception.Message}", exception);
            }
        }

        // Players still in the last topology first: a missing one was more likely unplugged or replaced.
        var knownSeeds = Players.Values
            .Where(player => player.BaseUri is not null)
            .OrderBy(player => player.IsInTopology ? 0 : 1)
            .Select(player => player.BaseUri!)
            .Distinct();

        foreach (var knownSeed in knownSeeds)
        {
            if (await ProbeSeedAsync(knownSeed, cancellationToken))
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

    private async Task<bool> ProbeSeedAsync(Uri seedUri, CancellationToken cancellationToken)
    {
        HttpClient httpClient;
        SonosClientProvider clientProvider;
        lock (_connectionsLock)
        {
            httpClient = _httpClient ?? throw new InvalidOperationException("No Sonos connection scope is open.");
            clientProvider = _clientProvider!;
        }

        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(SeedProbeTimeout);
        using var connection = new SonosConnection(seedUri, null, httpClient, clientProvider);
        try
        {
            await connection.ReadTopologyAsync(probeCancellation.Token);
            _logger.LogDebug("The known Sonos speaker {Uri} answered and becomes the seed.", seedUri);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The known Sonos speaker {Uri} did not answer.", seedUri);
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
        while (true)
        {
            if (await _configurationChanged.WaitAsync(EffectivePollingInterval, stoppingToken))
            {
                return true;
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
        }
    }

    private void MarkConnected()
    {
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
            _seedConnection = new SonosConnection(seedUri, null, _httpClient!, _clientProvider!);
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
            if (_httpClient is null || _clientProvider is null)
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

        _connections[device.Uuid] = new SonosConnection(baseUri, device.Uuid, _httpClient!, _clientProvider!);
        device.InvalidateStaticData();
    }

    private SonosConnection? FindConnection(string uuid)
    {
        lock (_connectionsLock)
        {
            return _connections.GetValueOrDefault(uuid);
        }
    }

    private bool IsCurrentConnection(string uuid, SonosConnection connection) =>
        ReferenceEquals(FindConnection(uuid), connection);

    private SonosEventListener? GetEventListener()
    {
        lock (_connectionsLock)
        {
            return _eventListener;
        }
    }

    private async Task PollPlayerAsync(SonosPlayer player, DateTimeOffset pollStartedAt, CancellationToken cancellationToken)
    {
        try
        {
            var connection = FindConnection(player.Uuid)
                ?? throw new InvalidOperationException("No connection to the player.");

            if (player.NeedsStaticData)
            {
                await ReadStaticDataAsync(player, connection, cancellationToken);
            }

            var reading = await connection.ReadPlayerAsync(player.IsHomeTheater, cancellationToken);
            var group = Groups.GetValueOrDefault(player.Uuid);
            var groupReading = group is null ? null : await connection.ReadGroupAsync(cancellationToken);

            // Teardown may have released the connection while the reads were in flight; their results must not
            // report the player reachable again.
            if (!IsCurrentConnection(player.Uuid, connection))
            {
                return;
            }

            player.ApplyPoll(reading, pollStartedAt);
            if (groupReading is not null)
            {
                group!.ApplyGroupRenderingControlPoll(groupReading, pollStartedAt);
            }

            player.ReportPollSucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Logged at Warning once per failure transition, so an offline player does not flood the log every poll.
            if (player.ReportPollFailed(exception.Message))
            {
                _logger.LogWarning(exception, "Polling the Sonos player in {Room} failed.", player.RoomName);
            }
            else
            {
                _logger.LogDebug(exception, "Polling the Sonos player in {Room} failed again.", player.RoomName);
            }
        }
    }

    private async Task PollSatelliteAsync(SonosSatellite satellite, CancellationToken cancellationToken)
    {
        try
        {
            var connection = FindConnection(satellite.Uuid)
                ?? throw new InvalidOperationException("No connection to the satellite.");

            await ReadStaticDataAsync(satellite, connection, cancellationToken);
            if (IsCurrentConnection(satellite.Uuid, connection))
            {
                satellite.ReportPollSucceeded();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (satellite.ReportPollFailed(exception.Message))
            {
                _logger.LogWarning(exception, "Reading the Sonos satellite {Uuid} failed.", satellite.Uuid);
            }
            else
            {
                _logger.LogDebug(exception, "Reading the Sonos satellite {Uuid} failed again.", satellite.Uuid);
            }
        }
    }

    private static async Task ReadStaticDataAsync(SonosDevice device, SonosConnection connection, CancellationToken cancellationToken)
    {
        var description = await connection.ReadDescriptionAsync(cancellationToken);
        var zoneInfo = await connection.ReadZoneInfoAsync(cancellationToken);
        device.ApplyStaticData(description, zoneInfo.SerialNumber, zoneInfo.MacAddress, zoneInfo.HardwareVersion, zoneInfo.DisplayVersion);
    }

    private async Task RefreshFavoritesAsync(SonosConnection seedConnection, CancellationToken cancellationToken)
    {
        try
        {
            SetFavorites(await seedConnection.ReadFavoritesAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Reading the Sonos favorites failed.");
        }
    }

    private async Task EnsureSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var eventListener = GetEventListener();
        if (eventListener is null || !eventListener.IsListening)
        {
            // Never started, or its accept loop died (which it logs); polling keeps the state current.
            AreEventsActive = false;
            ActiveEventCallbackHost = null;
            return;
        }

        var desired = GetDesiredSubscriptions();
        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in eventListener.Subscriptions)
        {
            if (!desired.TryGetValue(subscription.Key, out var target) || target.EventUri != subscription.EventUri)
            {
                await RunSubscriptionRequestAsync(() => eventListener.UnsubscribeAsync(subscription, cancellationToken), subscription.Key, cancellationToken);
            }
            else if (subscription.Sid is not null && subscription.RenewAt <= now)
            {
                await RunSubscriptionRequestAsync(() => eventListener.RenewAsync(subscription, cancellationToken), subscription.Key, cancellationToken);
            }
        }

        var active = eventListener.Subscriptions.Select(subscription => subscription.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, target) in desired)
        {
            if (!active.Contains(key))
            {
                await RunSubscriptionRequestAsync(() => eventListener.SubscribeAsync(key, target.EventUri, target.Handler, cancellationToken), key, cancellationToken);
            }
        }

        AreEventsActive = eventListener.Subscriptions.Any(subscription => subscription.Sid is not null);
    }

    private async Task RunSubscriptionRequestAsync(Func<Task> request, string key, CancellationToken cancellationToken)
    {
        try
        {
            await request();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Includes the listener's OperationCanceledException for a subscription unsubscribed concurrently.
            _logger.LogWarning(exception, "The Sonos event subscription {Key} failed; polling keeps its state current.", key);
        }
    }

    private Dictionary<string, (Uri EventUri, Action<string> Handler)> GetDesiredSubscriptions()
    {
        var desired = new Dictionary<string, (Uri EventUri, Action<string> Handler)>(StringComparer.Ordinal);
        var groups = Groups;
        foreach (var player in Players.Values)
        {
            if (!player.IsConnected || player.BaseUri is not { } baseUri)
            {
                continue;
            }

            desired[$"{player.Uuid}/{AvTransportService}"] =
                (new Uri(baseUri, "/MediaRenderer/AVTransport/Event"), body => OnAvTransportEvent(player, body));
            desired[$"{player.Uuid}/{RenderingControlService}"] =
                (new Uri(baseUri, "/MediaRenderer/RenderingControl/Event"), body => OnRenderingControlEvent(player, body));

            if (groups.ContainsKey(player.Uuid))
            {
                var coordinatorUuid = player.Uuid;
                desired[$"{player.Uuid}/{GroupRenderingControlService}"] =
                    (new Uri(baseUri, "/MediaRenderer/GroupRenderingControl/Event"), body => OnGroupRenderingControlEvent(coordinatorUuid, body));
            }
        }

        if (GetSeedUri() is { } seedUri)
        {
            desired[TopologySubscriptionKey] = (new Uri(seedUri, "/ZoneGroupTopology/Event"), OnTopologyEvent);
        }

        return desired;
    }

    // The event handlers run on the listener's thread pool callbacks, concurrently with polling. The listener catches
    // and logs a parser's XmlException, and its disposal waits for running handlers, so they must not block on it.

    private static void OnAvTransportEvent(SonosPlayer player, string body) =>
        player.ApplyAvTransportEvent(UpnpEventParser.ParseAvTransport(body), TimeProvider.System.GetUtcNow());

    private static void OnRenderingControlEvent(SonosPlayer player, string body) =>
        player.ApplyRenderingControlEvent(UpnpEventParser.ParseRenderingControl(body), TimeProvider.System.GetUtcNow());

    private void OnGroupRenderingControlEvent(string coordinatorUuid, string body)
    {
        if (Groups.TryGetValue(coordinatorUuid, out var group))
        {
            group.ApplyGroupRenderingControlEvent(UpnpEventParser.ParseGroupRenderingControl(body), TimeProvider.System.GetUtcNow());
        }
    }

    // New players found here get their connection and subscriptions at the next reconciliation.
    private void OnTopologyEvent(string body)
    {
        var zoneGroupState = UpnpEventParser.ParseZoneGroupState(body);
        if (!string.IsNullOrEmpty(zoneGroupState))
        {
            ApplyTopology(ZoneGroupStateParser.Parse(zoneGroupState));
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
