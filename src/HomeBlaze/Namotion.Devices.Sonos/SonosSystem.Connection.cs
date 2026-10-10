using System.Net;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Events;

namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    private const int MaxConsecutiveReconcileFailures = 3;
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

    // Never disposed, like _reconcileLock: neither exposes a wait handle, so there is nothing to release, and
    // disposing them while the loop is still unwinding from a Dispose without StopAsync made its last waits throw or
    // hang.
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);
    private readonly Lock _connectionsLock = new();

    // Guarded by _connectionsLock, as are the scope's connections and seed. Replaced for every connection attempt and
    // null between them; work that started on a scope checks that it is still the current one before it reports.
    private SonosConnectionScope? _scope;
    private bool _disposed;

    // The connection loop's current sleep. Cancelling it wakes the loop to reschedule when a reconciliation outside
    // the loop, from a command, schedules an earlier subscription wake. Guarded by _loopWakeLock.
    private readonly Lock _loopWakeLock = new();
    private CancellationTokenSource? _loopWake;

    // A persistent failure is logged at Warning once and at Debug while it lasts, see LogFailure.
    private readonly FailureTracker _failures = new();

    /// <summary>
    /// The longest polling or retry interval, so a hand-edited value cannot overflow the loop's waits.
    /// </summary>
    internal static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(MaximumIntervalSeconds);

    private TimeSpan EffectivePollingInterval => GetEffectiveInterval(PollingInterval, DefaultInterval, MinimumInterval);

    private TimeSpan EffectiveRetryInterval => GetEffectiveInterval(RetryInterval, DefaultInterval, MinimumInterval);

    /// <summary>
    /// Returns the configured interval clamped to <paramref name="minimum"/> through <see cref="MaximumInterval"/>,
    /// or <paramref name="fallback"/> when it is zero or negative.
    /// </summary>
    internal static TimeSpan GetEffectiveInterval(TimeSpan configured, TimeSpan fallback, TimeSpan minimum)
    {
        if (configured <= TimeSpan.Zero)
        {
            return fallback;
        }

        if (configured < minimum)
        {
            return minimum;
        }

        return configured > MaximumInterval ? MaximumInterval : configured;
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _configurationChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // Signalled already; one pending restart covers both.
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
                var scope = OpenConnectionScope();
                var seedUri = await FindSeedAsync(scope.HttpClient, stoppingToken)
                    ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(SeedHost)
                        ? "No Sonos speaker found. Set SeedHost when multicast discovery is blocked, for example under Docker bridge networking."
                        : $"The SeedHost '{SeedHost}' did not answer, and no other Sonos speaker was found.");

                await StartEventListenerAsync(scope.EventListener, seedUri.Host, stoppingToken);
                reconnectImmediately = await RunConnectedAsync(scope, seedUri, stoppingToken);
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

    private SonosConnectionScope OpenConnectionScope()
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

            _scope = new SonosConnectionScope(
                httpClient,
                new SonosEventListener(httpClient, _logger, MinimumSubscriptionLifetime, Clock, UpdateAreEventsActive));
            return _scope;
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
            return _scope is { } scope
                ? CancellationTokenSource.CreateLinkedTokenSource(scope.Cancellation.Token, cancellationToken)
                : throw CreateNotConnectedException();
        }
    }

    private async Task CloseConnectionScopeAsync()
    {
        IsConnected = false;
        AreEventsActive = false;
        ActiveEventCallbackHost = null;

        // First, so a reconciliation or refresh running on a caller's token releases the lock promptly.
        SonosConnectionScope? scope;
        lock (_connectionsLock)
        {
            scope = _scope;
        }

        if (scope is not null)
        {
            await scope.Cancellation.CancelAsync();
        }

        // The stopping token is already cancelled on shutdown, so teardown gets its own short budgets, one for the
        // lock and one for the unsubscribes, so a slow reconciliation cannot use up the time for unsubscribing. Holding
        // the reconcile lock keeps a command's reconciliation from subscribing again between unsubscribing and disposing.
        var hasReconcileLock = await _reconcileLock.WaitAsync(TeardownTimeout, CancellationToken.None);
        if (!hasReconcileLock)
        {
            _logger.LogWarning("A Sonos reconciliation did not finish within the teardown budget; tearing down anyway.");
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

    private async Task ReleaseConnectionScopeAsync(CancellationToken cancellationToken)
    {
        // Again, after waiting for an in-flight reconciliation or for the budget to expire: one that was in flight
        // may have set it after the early reset.
        AreEventsActive = false;
        _failures.ReportSuccess(EventDeliveryFailureKey);
        Interlocked.Exchange(ref _nextWakeAt, TimeProviderExtensions.Never);

        // Detached first: nothing changes a scope that is no longer current, so it is disposed without the lock, and a
        // late poll result finds its connection gone.
        SonosConnectionScope? scope;
        lock (_connectionsLock)
        {
            scope = _scope;
            _scope = null;
        }

        if (scope is not null)
        {
            try
            {
                await scope.EventListener.UnsubscribeAllAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unsubscribing the Sonos events failed; the speakers drop them once they expire.");
            }

            await scope.DisposeAsync();

            // After the handlers drained: a first event that arrived during teardown may have set it.
            UpdateAreEventsActive();
        }

        foreach (var device in GetDevices(Players.Values))
        {
            device.MarkUnreachable(DisconnectedMessage);
            device.ForgetStateOfOutage();
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
    private async Task<Uri?> FindSeedAsync(HttpClient httpClient, CancellationToken cancellationToken)
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
            if (await ProbeSeedAsync(httpClient, seedHostUri, RequestTimeout, cancellationToken))
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
            if (await ProbeSeedAsync(httpClient, knownSeed, SeedProbeTimeout, cancellationToken))
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

    private async Task<bool> ProbeSeedAsync(HttpClient httpClient, Uri seedUri, TimeSpan timeout, CancellationToken cancellationToken)
    {
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

    private async Task StartEventListenerAsync(SonosEventListener eventListener, string seedHost, CancellationToken cancellationToken)
    {
        var callbackHost = string.IsNullOrWhiteSpace(EventCallbackHost)
            ? await SonosDiscovery.DetectLocalAddressAsync(seedHost, cancellationToken)
            : EventCallbackHost.Trim();

        if (callbackHost is null)
        {
            _logger.LogWarning("No local IPv4 address routes to the Sonos speaker {Host}; continuing with polling only.", seedHost);
            return;
        }

        try
        {
            eventListener.Start(callbackHost, EventPort, EventListenHost);
            ActiveEventCallbackHost = callbackHost;
        }
        catch (HttpListenerException exception)
        {
            // On Windows, listening on all interfaces needs a URL ACL or elevation.
            _logger.LogWarning(exception, "The Sonos event listener could not listen on port {Port}; continuing with polling only.", EventPort);
        }
    }

    private async Task<bool> RunConnectedAsync(SonosConnectionScope scope, Uri seedUri, CancellationToken stoppingToken)
    {
        SetSeed(scope, seedUri);

        // The first reconciliation is what establishes the connection, so its failure ends this attempt.
        await ReconcileAsync(stoppingToken);
        MarkConnected();

        var consecutiveFailures = 0;
        var nextPollAt = Clock.GetTimestampAfter(EffectivePollingInterval);
        while (true)
        {
            bool isSubscriptionWake;
            using (var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                lock (_loopWakeLock)
                {
                    _loopWake = wake;
                }

                try
                {
                    // Read after publishing the wake source: a wake scheduled before is seen here, one scheduled
                    // after cancels the wait.
                    var wait = GetLoopWait(nextPollAt, out isSubscriptionWake);
                    if (await _configurationChanged.WaitAsync(wait, wake.Token))
                    {
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Woken for an earlier subscription wake; the next iteration waits for it.
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

            if (isSubscriptionWake)
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
                SetSeed(scope, SelectNextSeed());
            }

            nextPollAt = Clock.GetTimestampAfter(EffectivePollingInterval);
        }
    }

    // Subscriptions live 30 minutes, which a long polling interval would let lapse, so the loop also wakes to renew
    // them, without a full poll.
    private TimeSpan GetLoopWait(long nextPollAt, out bool isSubscriptionWake)
    {
        var nextWakeAt = Interlocked.Read(ref _nextWakeAt);
        isSubscriptionWake = nextWakeAt < nextPollAt;
        var wait = Clock.GetTimeUntil(isSubscriptionWake ? nextWakeAt : nextPollAt);
        return wait > MinimumLoopWait ? wait : MinimumLoopWait;
    }

    private void WakeLoop()
    {
        lock (_loopWakeLock)
        {
            // Asynchronous, so the loop never continues on this thread; the loop disposes the source only after it
            // cleared the field under this lock.
            _ = _loopWake?.CancelAsync();
        }
    }

    private void MarkConnected()
    {
        _failures.ReportSuccess(ConnectionFailureKey);
        IsConnected = true;
        Status = ServiceStatus.Running;
        StatusMessage = null;
    }

    // Called by the connection loop, which owns the scope while it is open.
    private void SetSeed(SonosConnectionScope scope, Uri seedUri)
    {
        lock (_connectionsLock)
        {
            if (scope.Seed?.BaseUri == seedUri)
            {
                return;
            }

            scope.Seed?.Dispose();
            scope.Seed = new SonosConnection(seedUri, null, scope.HttpClient);
        }
    }

    private SonosConnection GetSeedConnection()
    {
        lock (_connectionsLock)
        {
            return _scope?.Seed ?? throw new InvalidOperationException("No Sonos seed speaker is selected.");
        }
    }

    private Uri? GetSeedUri()
    {
        lock (_connectionsLock)
        {
            return _scope?.Seed?.BaseUri;
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
            if (_scope is not { } scope)
            {
                return;
            }

            foreach (var device in GetDevices(Players.Values))
            {
                SyncConnection(scope, device);
            }
        }
    }

    // Caller holds _connectionsLock.
    private void SyncConnection(SonosConnectionScope scope, SonosDevice device)
    {
        if (!device.IsInTopology || device.BaseUri is not { } baseUri)
        {
            return;
        }

        if (scope.Connections.TryGetValue(device.Uuid, out var existing))
        {
            if (existing.BaseUri == baseUri)
            {
                return;
            }

            existing.Dispose();
        }

        scope.Connections[device.Uuid] = new SonosConnection(baseUri, device.Uuid, scope.HttpClient, _logger);
        device.InvalidateStaticData();
    }

    private SonosConnection? FindConnection(string uuid)
    {
        lock (_connectionsLock)
        {
            return _scope?.Connections.GetValueOrDefault(uuid);
        }
    }

    private SonosEventListener? GetEventListener()
    {
        lock (_connectionsLock)
        {
            return _scope?.EventListener;
        }
    }

    private void LogFailure(bool isNew, Exception? exception, string message, params object?[] args) =>
        _logger.Log(isNew ? LogLevel.Warning : LogLevel.Debug, exception, message, args);

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
