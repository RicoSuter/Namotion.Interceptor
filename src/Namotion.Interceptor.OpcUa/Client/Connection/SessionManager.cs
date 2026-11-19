using System.Collections.Concurrent;
using Opc.Ua.Security.Certificates;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// Owns the OPC UA session. Reconnection, session recreation and subscription restoration are driven
/// by the SDK's <see cref="ManagedSession"/>; this class maps its state transitions onto the source's
/// liveness, reconnection metrics and full state synchronization.
/// </summary>
internal sealed class SessionManager : IAsyncDisposable, IDisposable
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly SubjectPropertyWriter _propertyWriter;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    // Swapped only by SetSession. The session and its lifetime are one object, so a reader never pairs a
    // session with another session's lifetime.
    private SessionLease? _sessionLease;

    // Serializes liveness reports with the state-change callback, so a stale read in
    // ReportLivenessFromSessionState cannot raise liveness after a transition dropped it.
    private readonly Lock _stateLock = new();

    // Transitions are enqueued by SetSession and drained outside any lock, so user-supplied
    // CurrentSessionChanged handlers never run with a lock held.
    private readonly ConcurrentQueue<(ISession? Previous, ISession? Current)> _pendingSessionTransitions = new();
    private readonly Lock _drainLock = new();

    // The managed session's own Connected only says that a server session id exists, which stays true
    // through an outage, so the state is tracked from its transitions.
    private int _connectionState = (int)ConnectionState.Disconnected;
    private ServiceResult? _lastReconnectError; // guarded by _stateLock
    private int _reconnectInProgress; // 1 = a reconnect cycle has not resolved yet
    private int _needsFullStateSync;
    private int _responseMismatchSyncPending; // 0 = false, 1 = true (thread-safe via Interlocked)
    private long? _lastFullStateSyncTimestamp; // Health check loop only
    private Certificate? _clientCertificate; // Replaced and read under _stateLock; owned by this instance
    private long _lastClientCertificateCheckTimestamp; // Health check loop and session creation
    private long _lastExpiryWarningTimestamp; // Health check loop and session creation
    private int _clientCertificateCheckRequested; // 1 = check on the next health check tick
    private int _disposed;

    // Owns the certificate manager and its timer, so it is disposed with this manager.
    private ApplicationInstance? _application;

    /// <summary>
    /// Gets the current session. The managed session stays the same object across reconnects and
    /// session recreation; it is only replaced when the source restarts.
    /// </summary>
    public ManagedSession? CurrentSession => Volatile.Read(ref _sessionLease)?.Session;

    /// <summary>
    /// Gets the current session with a token that is canceled once that session stops being current, or
    /// <c>default</c> without a session.
    /// </summary>
    internal (ISession? Session, CancellationToken Lifetime) CurrentSessionWithLifetime =>
        Volatile.Read(ref _sessionLease) is { } lease ? (lease.Session, lease.Lifetime) : default;

    /// <summary>
    /// Gets a value indicating whether the session is connected and not reconnecting.
    /// </summary>
    public bool IsConnected =>
        CurrentSession is { Connected: true } &&
        (ConnectionState)Volatile.Read(ref _connectionState) is ConnectionState.Connected;

    /// <summary>
    /// Gets a value indicating whether the managed session is recovering the connection, including
    /// the pause between two reconnection cycles.
    /// </summary>
    public bool IsReconnecting =>
        CurrentSession is not null &&
        (ConnectionState)Volatile.Read(ref _connectionState)
            is ConnectionState.Reconnecting or ConnectionState.Failover or ConnectionState.Disconnected;

    /// <summary>
    /// Gets a value indicating whether this manager has been disposed.
    /// </summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// Gets the current subscriptions managed by the subscription manager.
    /// </summary>
    public IReadOnlyCollection<Subscription> Subscriptions => SubscriptionManager.Subscriptions;

    internal SubscriptionManager SubscriptionManager { get; }

    internal PollingManager? PollingManager { get; }

    internal ReadAfterWriteManager? ReadAfterWriteManager { get; }

    internal PollingDiagnostics? PollingDiagnostics { get; }

    internal ReadAfterWriteDiagnostics? ReadAfterWriteDiagnostics { get; }

    public SessionManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        PollingMetrics pollingMetrics,
        ReadAfterWriteMetrics readAfterWriteMetrics,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _source = source;
        _propertyWriter = propertyWriter;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _configuration = configuration;

        // The polling and read-after-write reads report a response to a different request through their
        // error callback, which therefore also schedules the full state sync.
        Action<Exception> reportReadError = error =>
        {
            if (error is OpcUaResponseMismatchException)
            {
                ScheduleFullStateSyncAfterResponseMismatch();
            }

            source.ReportBackgroundError(error);
        };

        if (_configuration.EnablePollingFallback)
        {
            PollingManager = new PollingManager(
                source,
                sessionProvider: () => CurrentSessionWithLifetime,
                propertyWriter,
                _configuration,
                pollingMetrics,
                reportReadError,
                _logger);

            PollingDiagnostics = new PollingDiagnostics(PollingManager, pollingMetrics);

            PollingManager.Start();
        }

        if (_configuration.EnableReadAfterWrite)
        {
            ReadAfterWriteManager = new ReadAfterWriteManager(
                sessionProvider: () => CurrentSessionWithLifetime,
                source,
                configuration,
                readAfterWriteMetrics,
                reportReadError,
                logger);

            ReadAfterWriteDiagnostics = new ReadAfterWriteDiagnostics(ReadAfterWriteManager, readAfterWriteMetrics);
        }

        SubscriptionManager = new SubscriptionManager(
            source,
            propertyWriter,
            PollingManager,
            ReadAfterWriteManager,
            configuration,
            source.ReportBackgroundError,
            logger);
    }

    /// <summary>
    /// The minimum time between two full state syncs scheduled by <see cref="ScheduleFullStateSyncAfterResponseMismatch"/>.
    /// </summary>
    internal static readonly TimeSpan ResponseMismatchSyncInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Performs a full state synchronization if one is pending: after the managed session recovered the
    /// connection, after a subscription recreate, or after a response to a different request was received.
    /// Buffers incoming notifications, reads all property values from the server, replays the buffer
    /// and resumes. On failure a sync is requested again, so the next health check tick retries.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="alreadyBuffering">
    /// True when the caller started buffering itself, so the notifications buffered since are replayed
    /// instead of being discarded by starting over.
    /// </param>
    internal async Task PerformFullStateSyncIfNeededAsync(CancellationToken cancellationToken, bool alreadyBuffering = false)
    {
        // Taken before the load rather than cleared after it: a recovery that happens while the load
        // runs requests another sync, and the load it supersedes returns without applying anything.
        // A caller that started buffering always loads, so buffering cannot outlive it.
        var requested = Interlocked.Exchange(ref _needsFullStateSync, 0) == 1 || alreadyBuffering;
        if (!requested && !IsResponseMismatchSyncDue())
        {
            return;
        }

        var reason = requested ? "after OPC UA session recovery" : "after a response to a different request was received";

        // Cleared before the read, so a mismatch detected while it runs schedules the next sync.
        Interlocked.Exchange(ref _responseMismatchSyncPending, 0);
        _lastFullStateSyncTimestamp = _timeProvider.GetTimestamp();

        _logger.LogInformation("Performing full state sync {Reason}...", reason);
        if (!alreadyBuffering)
        {
            _propertyWriter.StartBuffering();
        }

        try
        {
            await _propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Full state sync completed successfully {Reason}.", reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A recovery or a recreate retries on the next tick. A sync after a response mismatch retries through the
            // mismatch path, so a server that keeps answering wrongly still gets at most one sync per interval.
            if (requested)
            {
                Interlocked.Exchange(ref _needsFullStateSync, 1);
            }
            else
            {
                Interlocked.Exchange(ref _responseMismatchSyncPending, 1);
            }

            // Buffered notifications are newer than anything the failed load could have applied, so they are applied
            // now instead of being withheld until the retry; the source stays Synchronizing until a sync succeeds.
            _propertyWriter.ResumeWithoutLoad();

            _source.ReportBackgroundError(ex, cancellationToken);
            _logger.LogError(
                ex, "Full state sync failed {Reason}. Retrying on the next health check{Delay}.",
                reason, requested ? string.Empty : $" after at least {ResponseMismatchSyncInterval.TotalSeconds:0} seconds");
        }
    }

    /// <summary>
    /// Schedules a full state sync after a response to a different request was received, which may have
    /// cost this client the response to another of its requests, such as subscription notifications.
    /// </summary>
    /// <remarks>
    /// Thread-safe. The sync runs on the health check loop once the session is healthy. Repeated calls
    /// coalesce into one pending sync, which runs no sooner than <see cref="ResponseMismatchSyncInterval"/>
    /// after the previous full state sync, so a server that keeps answering with wrong responses cannot
    /// cause a sync storm.
    /// </remarks>
    internal void ScheduleFullStateSyncAfterResponseMismatch() =>
        Interlocked.Exchange(ref _responseMismatchSyncPending, 1);

    private bool IsResponseMismatchSyncDue() =>
        Volatile.Read(ref _responseMismatchSyncPending) == 1 &&
        (_lastFullStateSyncTimestamp is not { } lastTimestamp ||
         _timeProvider.GetElapsedTime(lastTimestamp) >= ResponseMismatchSyncInterval);

    /// <summary>
    /// Requests a full state synchronization on the next <see cref="PerformFullStateSyncIfNeededAsync"/> call.
    /// </summary>
    internal void RequestFullStateSync() => Interlocked.Exchange(ref _needsFullStateSync, 1);

    /// <summary>
    /// Reports the source's liveness from the current session state: raised when the session is
    /// connected and not reconnecting, dropped otherwise.
    /// </summary>
    internal void ReportLivenessFromSessionState()
    {
        lock (_stateLock)
        {
            if (IsConnected)
            {
                _source.NotifySessionHealthy();
            }
            else
            {
                _source.NotifySessionNotHealthy();
            }
        }
    }

    /// <summary>
    /// Creates the managed session. The managed session then owns reconnection for its lifetime.
    /// </summary>
    public async Task<ManagedSession> CreateSessionAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var applicationConfiguration = application.ApplicationConfiguration
            ?? throw new InvalidOperationException("The OPC UA application instance has no application configuration.");
        Volatile.Write(ref _application, application);
        var endpointConfiguration = EndpointConfiguration.Create(applicationConfiguration);
        var serverUri = new Uri(configuration.ServerUrl);

        ArrayOf<EndpointDescription> endpoints;
        try
        {
            using var discoveryClient = await DiscoveryClient.CreateAsync(
                applicationConfiguration,
                serverUri,
                endpointConfiguration, ct: cancellationToken).ConfigureAwait(false);

            endpoints = await discoveryClient.GetEndpointsAsync(default, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to discover OPC UA endpoints at '{configuration.ServerUrl}'. " +
                $"Verify the server is running and the URL is correct. " +
                $"The connection will be retried automatically.",
                ex);
        }

        var endpointDescription = SelectEndpoint(
            applicationConfiguration,
            serverUri,
            endpoints,
            configuration.MinimumSecurityMode,
            configuration.ResolvedTelemetryContext);

        // Discovery needs no client certificate, and an unsecured endpoint never does. While the source runs,
        // CheckClientCertificateIfDueAsync repeats the check and hands a replacement to the managed session.
        using var clientCertificate = endpointDescription.SecurityMode != MessageSecurityMode.None
            ? await LoadClientCertificateAsync(application, applicationConfiguration, configuration, cancellationToken).ConfigureAwait(false)
            : null;

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        // Resolved before the try so a ServiceResultException from user code is not reported as a certificate error.
        var identity = configuration.CreateUserIdentity != null
            ? await configuration.CreateUserIdentity(cancellationToken).ConfigureAwait(false)
            : new UserIdentity();

        // One policy cycle is one reconnect attempt in the diagnostics: when its budget is exhausted the
        // managed session reports Disconnected and starts the next cycle by itself.
        var reconnectPolicy = new ReconnectPolicy
        {
            Strategy = BackoffStrategy.Constant,
            InitialDelay = configuration.ReconnectInterval,
            MaxDelay = configuration.ReconnectInterval,
            MaxRetries = 0,
            MaxTotalReconnectTime = configuration.MaxReconnectDuration
        };

        ManagedSession session;
        try
        {
            session = await ManagedSession.CreateAsync(
                applicationConfiguration,
                endpoint,
                configuration.ActualSessionFactory,
                identity: identity,
                reconnectPolicy: reconnectPolicy,
                telemetry: configuration.ResolvedTelemetryContext,
                sessionName: application.ApplicationName ?? configuration.ApplicationName,
                sessionTimeout: (uint)configuration.SessionTimeout.TotalMilliseconds,
                engineFactory: ClassicSubscriptionEngineFactory.Instance,
                ct: cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException error) when (IsCertificateError(error, endpointDescription))
        {
            throw CreateCertificateErrorException(
                error, endpointDescription, applicationConfiguration, clientCertificate, configuration.ResolvedTelemetryContext);
        }

        ReplaceClientCertificate(clientCertificate?.AddRef());
        Interlocked.Exchange(ref _lastClientCertificateCheckTimestamp, _timeProvider.GetTimestamp());

        session.TransferSubscriptionsOnReconnect = true;
        session.DeleteSubscriptionsOnClose = false;
        session.MinPublishRequestCount = _configuration.MinPublishRequestCount;
        session.KeepAliveInterval = (int)_configuration.KeepAliveInterval.TotalMilliseconds;
        Volatile.Write(ref _connectionState, (int)ConnectionState.Connected);
        var previousSession = SetSession(session);

        // Attached after the session is published, so the handlers do not drop its transitions as
        // coming from a foreign session; a connection loss raised before that is caught up below.
        session.ConnectionStateChanged += OnConnectionStateChanged;
        session.SessionGenerationChanged += OnSessionGenerationChanged;
        CatchUpMissedConnectionLoss(session);

        DrainPendingSessionTransitions();

        if (previousSession is not null)
        {
            await DisposeSessionAsync(previousSession, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    private void CatchUpMissedConnectionLoss(ManagedSession session)
    {
        // Read under the lock the handler runs under: the managed session sets its state before it
        // raises the change, so a recovery that already ran its handler is seen here as recovered.
        lock (_stateLock)
        {
            if (!session.Reconnecting ||
                Volatile.Read(ref _disposed) == 1 ||
                !ReferenceEquals(session, CurrentSession) ||
                (ConnectionState)Volatile.Read(ref _connectionState) is not ConnectionState.Connected)
            {
                return;
            }

            Volatile.Write(ref _connectionState, (int)ConnectionState.Reconnecting);
            if (Interlocked.Exchange(ref _reconnectInProgress, 1) == 0)
            {
                _source.NotifyConnectionLost();
                _source.ReconnectionMetrics.RecordAttemptStart();
            }
        }
    }

    private async Task<Certificate> LoadClientCertificateAsync(
        ApplicationInstance application,
        ApplicationConfiguration applicationConfiguration,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Read before loading, because a successful load sets the subject of the certificate it found.
        var subjectName = applicationConfiguration.SecurityConfiguration.ApplicationCertificate?.SubjectName;
        var lastExpiryWarning = Interlocked.Read(ref _lastExpiryWarningTimestamp);
        var logExpiryWarning = lastExpiryWarning == 0 || _timeProvider.GetElapsedTime(lastExpiryWarning) >= ExpiryWarningInterval;
        try
        {
            var certificate = await ApplicationCertificateLoader.EnsureLoadedAsync(
                application,
                configuration.AutoCreateApplicationCertificate,
                configuration.ApplicationCertificateLifetimeInMonths,
                configuration.ResolvedTelemetryContext,
                _logger,
                logExpiryWarning,
                cancellationToken).ConfigureAwait(false);
            if (logExpiryWarning && ApplicationCertificateLoader.IsWithinExpiryWarningWindow(certificate))
            {
                Interlocked.Exchange(ref _lastExpiryWarningTimestamp, _timeProvider.GetTimestamp());
            }

            return certificate;
        }
        // The SDK can report a cancellation as another exception type, so the token is checked too.
        catch (Exception error) when (error is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw CreateCertificateLoadException(
                error, applicationConfiguration, subjectName, configuration.AutoCreateApplicationCertificate);
        }
    }

    /// <summary>
    /// The interval at which <see cref="CheckClientCertificateIfDueAsync"/> checks the client certificate of a secured
    /// session when no reconnect cycle failed with a certificate error.
    /// </summary>
    internal static readonly TimeSpan ClientCertificateCheckInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// The minimum time between two checks requested by reconnect cycles that failed with a certificate error. The
    /// status codes do not tell a rejected client certificate from a rejected server certificate, so every such cycle
    /// requests a check.
    /// </summary>
    internal static readonly TimeSpan RequestedClientCertificateCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The minimum time between two expiry warnings for the client certificate.
    /// </summary>
    internal static readonly TimeSpan ExpiryWarningInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Checks the client certificate of a secured session once per <see cref="ClientCertificateCheckInterval"/>, and
    /// on the next call after a reconnect cycle failed with a certificate error. The check logs the expiry warning and,
    /// in a managed directory store, replaces an expired or mismatching self-signed certificate. A certificate that
    /// differs from the one in use is passed to the certificate manager, which makes the managed session reload it
    /// and reconnect. A failure is reported as a background error.
    /// </summary>
    internal async Task CheckClientCertificateIfDueAsync(CancellationToken cancellationToken)
    {
        var application = Volatile.Read(ref _application);
        var currentThumbprint = GetClientCertificateThumbprint();
        if (application?.ApplicationConfiguration is not { } applicationConfiguration || currentThumbprint is null)
        {
            return;
        }

        var requested = Volatile.Read(ref _clientCertificateCheckRequested) == 1;
        var lastCheck = Interlocked.Read(ref _lastClientCertificateCheckTimestamp);
        if (lastCheck != 0 &&
            _timeProvider.GetElapsedTime(lastCheck) < (requested ? RequestedClientCertificateCheckInterval : ClientCertificateCheckInterval))
        {
            return;
        }

        Interlocked.Exchange(ref _clientCertificateCheckRequested, 0);

        Interlocked.Exchange(ref _lastClientCertificateCheckTimestamp, _timeProvider.GetTimestamp());
        try
        {
            using var certificate = await LoadClientCertificateAsync(
                application, applicationConfiguration, _configuration, cancellationToken).ConfigureAwait(false);
            if (string.Equals(certificate.Thumbprint, currentThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // A certificate the SDK created while loading is already registered with the certificate manager, which
            // notified the managed session itself; updating it again would make the managed session reconnect twice.
            if (IsRegisteredWithCertificateManager(applicationConfiguration, certificate))
            {
                _logger.LogInformation(
                    "The OPC UA client certificate changed from {PreviousThumbprint} to {Thumbprint}, which the certificate " +
                    "manager already holds. Reconnecting with it.",
                    currentThumbprint, certificate.Thumbprint);
                ReplaceClientCertificate(certificate.AddRef());
                return;
            }

            if (applicationConfiguration.CertificateManager is not ICertificateLifecycle lifecycle)
            {
                _logger.LogWarning(
                    "The OPC UA client certificate changed to {Thumbprint}, but the application has no certificate manager " +
                    "to pass it to. The client uses it once the source restarts.",
                    certificate.Thumbprint);
                return;
            }

            _logger.LogInformation(
                "The OPC UA client certificate changed from {PreviousThumbprint} to {Thumbprint}. Reconnecting with it.",
                currentThumbprint, certificate.Thumbprint);

            // The change event carries this handle to observers that may read it later, so it stays alive as the
            // retained certificate instead of the one disposed when this method returns.
            var retained = certificate.AddRef();
            try
            {
                await lifecycle.UpdateApplicationCertificateAsync(
                    CertificateIdentifier.GetCertificateType(retained), retained, ct: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                retained.Dispose();
                throw;
            }

            ReplaceClientCertificate(retained);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _source.ReportBackgroundError(ex, cancellationToken);
            _logger.LogError(ex, "Failed to check the OPC UA client certificate. Retrying in {Interval}.", ClientCertificateCheckInterval);
        }
    }

    private static bool IsRegisteredWithCertificateManager(ApplicationConfiguration applicationConfiguration, Certificate certificate)
    {
        if (applicationConfiguration.CertificateManager is not ICertificateRegistry registry)
        {
            return false;
        }

        using var registered = registry.AcquireApplicationCertificateByType(CertificateIdentifier.GetCertificateType(certificate));
        return registered is not null &&
            string.Equals(registered.Certificate.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    private string? GetClientCertificateThumbprint()
    {
        lock (_stateLock)
        {
            return _clientCertificate?.Thumbprint;
        }
    }

    /// <summary>
    /// Replaces the retained client certificate with <paramref name="certificate"/>, taking ownership of it.
    /// </summary>
    private void ReplaceClientCertificate(Certificate? certificate)
    {
        Certificate? previous;
        lock (_stateLock)
        {
            previous = _clientCertificate;
            _clientCertificate = certificate;
        }

        previous?.Dispose();
    }

    public async Task CreateSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        ISession session, CancellationToken cancellationToken)
    {
        await SubscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Created {SubscriptionCount} subscriptions with {Subscribed} " +
            "total monitored items ({Polled} via polling).",
            SubscriptionManager.SubscriptionCount,
            SubscriptionManager.MonitoredItemCount,
            PollingManager?.PollingItemCount ?? 0);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _logger.LogInformation(
            "OPC UA connection state changed from {PreviousState} to {NewState} (attempt {Attempt}).",
            e.PreviousState, e.NewState, e.ReconnectAttempt);

        lock (_stateLock)
        {
            // Checked under the lock DisposeAsync settles the open attempt under, so no attempt can
            // start after that.
            if (Volatile.Read(ref _disposed) == 1 || !ReferenceEquals(sender, CurrentSession))
            {
                return;
            }

            Volatile.Write(ref _connectionState, (int)e.NewState);

            switch (e.NewState)
            {
                case ConnectionState.Reconnecting or ConnectionState.Failover:
                    if (e.Error is not null && ServiceResult.IsBad(e.Error))
                    {
                        _lastReconnectError = e.Error;
                    }

                    if (Interlocked.Exchange(ref _reconnectInProgress, 1) == 0)
                    {
                        _source.NotifyConnectionLost();
                        _source.ReconnectionMetrics.RecordAttemptStart();
                    }
                    else
                    {
                        _source.NotifySessionNotHealthy();
                    }
                    break;

                case ConnectionState.Disconnected:
                    // The reconnect policy's budget is exhausted. The managed session starts a new
                    // cycle by itself, which is counted as the next attempt.
                    if (Interlocked.Exchange(ref _reconnectInProgress, 0) == 1)
                    {
                        _source.ReconnectionMetrics.RecordFailure();

                        // The error of the last failed connect attempt of the cycle: the one this
                        // transition carries is the result of the failover that follows it.
                        var error = _lastReconnectError ?? e.Error;
                        if (error is not null)
                        {
                            _source.ReportBackgroundError(CreateReconnectCycleException((ManagedSession)sender!, error));
                        }
                    }

                    _lastReconnectError = null;
                    _source.NotifySessionNotHealthy();
                    break;

                case ConnectionState.Connected:
                    _lastReconnectError = null;
                    if (Interlocked.Exchange(ref _reconnectInProgress, 0) == 1)
                    {
                        _source.ReconnectionMetrics.RecordSuccess();
                    }

                    // Notifications can be lost during an outage even when the server session and
                    // its subscriptions survived, so every recovery ends with a full read.
                    Interlocked.Exchange(ref _needsFullStateSync, 1);

                    if (IsConnected)
                    {
                        _source.NotifySessionHealthy();
                    }
                    break;

                default:
                    _source.NotifySessionNotHealthy();
                    break;
            }
        }
    }

    /// <summary>
    /// Creates the error reported for a reconnect cycle that ended without a connection, and requests a check of the
    /// client certificate when the cycle failed with a certificate error. Called under <see cref="_stateLock"/>.
    /// </summary>
    private Exception CreateReconnectCycleException(ManagedSession session, ServiceResult error)
    {
        var exception = CreateReconnectCycleException(
            error,
            session.ConfiguredEndpoint?.Description,
            Volatile.Read(ref _application)?.ApplicationConfiguration,
            _clientCertificate,
            _configuration.ResolvedTelemetryContext);
        if (exception is InvalidOperationException)
        {
            Interlocked.Exchange(ref _clientCertificateCheckRequested, 1);
        }

        return exception;
    }

    /// <summary>
    /// Creates the error reported for a reconnect cycle that ended with <paramref name="error"/>: a certificate error
    /// is explained by <see cref="CreateCertificateErrorException"/>, like one on the initial connect, and any other
    /// error is reported as is.
    /// </summary>
    internal static Exception CreateReconnectCycleException(
        ServiceResult error,
        EndpointDescription? endpoint,
        ApplicationConfiguration? applicationConfiguration,
        Certificate? clientCertificate,
        ITelemetryContext telemetryContext)
    {
        var exception = new ServiceResultException(error);
        return endpoint is not null && applicationConfiguration is not null && IsCertificateError(exception, endpoint)
            ? CreateCertificateErrorException(exception, endpoint, applicationConfiguration, clientCertificate, telemetryContext)
            : exception;
    }

    private void OnSessionGenerationChanged(object? sender, SessionGenerationChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 1 || sender is not ManagedSession session ||
            !ReferenceEquals(session, CurrentSession))
        {
            return;
        }

        if (e.PreviousSessionId.IsNull || e.PreviousSessionId == e.SessionId)
        {
            return;
        }

        _logger.LogInformation(
            "OPC UA server session recreated ({PreviousSessionId} -> {SessionId}). Full state sync pending.",
            e.PreviousSessionId, e.SessionId);

        // The managed session restores the subscriptions onto the new server session in place, where
        // they number their messages from the start again. Notifications may have been missed while
        // no server session existed, hence the full read.
        SubscriptionManager.ResetNotificationRanking();
        ReadAfterWriteManager?.ClearPendingReads();
        Interlocked.Exchange(ref _needsFullStateSync, 1);
    }

    /// <summary>
    /// Selects the endpoint to connect to among those with at least <paramref name="minimumSecurityMode"/>. With
    /// <see cref="MessageSecurityMode.None"/> an unsecured endpoint is preferred; otherwise, or when the server offers
    /// none, the most secure endpoint with a security policy the client supports is selected.
    /// </summary>
    /// <exception cref="InvalidOperationException">No endpoint matches the scheme, none has the minimum security mode,
    /// or those that have it use only security policies the client does not support.</exception>
    internal static EndpointDescription SelectEndpoint(
        ApplicationConfiguration applicationConfiguration,
        Uri serverUri,
        ArrayOf<EndpointDescription> endpoints,
        MessageSecurityMode minimumSecurityMode,
        ITelemetryContext telemetryContext)
    {
        var schemeEndpoints = endpoints.ToList()
            .Where(endpoint => endpoint.EndpointUrl?.StartsWith(serverUri.Scheme, StringComparison.Ordinal) == true)
            .ToArray();

        if (schemeEndpoints.Length == 0)
        {
            throw new InvalidOperationException(
                $"The OPC UA server at '{serverUri}' offers no endpoint for the '{serverUri.Scheme}' scheme.");
        }

        // The SDK falls back to the first endpoint whatever its mode and policy, so weaker ones are filtered out first.
        var candidates = schemeEndpoints.Where(endpoint => endpoint.SecurityMode >= minimumSecurityMode).ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"The OPC UA server at '{serverUri}' offers no endpoint with the security mode {minimumSecurityMode} or stronger, " +
                $"which {nameof(OpcUaClientConfiguration.MinimumSecurityMode)} requires. " +
                $"The server offers {DescribeEndpoints(schemeEndpoints)}.");
        }

        if (minimumSecurityMode == MessageSecurityMode.None &&
            candidates.FirstOrDefault(endpoint => endpoint.SecurityMode == MessageSecurityMode.None) is { } unsecuredEndpoint)
        {
            return unsecuredEndpoint;
        }

        var supportedPolicies = applicationConfiguration.SecurityConfiguration.SupportedSecurityPolicies.ToArray() ?? [];
        var securedEndpoint = CoreClientUtils.SelectEndpoint(applicationConfiguration, serverUri, candidates, true, telemetryContext);
        if (securedEndpoint is null || !supportedPolicies.Contains(securedEndpoint.SecurityPolicyUri, StringComparer.Ordinal))
        {
            var requirement = minimumSecurityMode == MessageSecurityMode.None
                ? "no unsecured endpoint and no secured endpoint with"
                : $"no endpoint with the security mode {minimumSecurityMode} or stronger (required by " +
                  $"{nameof(OpcUaClientConfiguration.MinimumSecurityMode)}) and";

            throw new InvalidOperationException(
                $"The OPC UA server at '{serverUri}' offers {requirement} a security policy this client supports. " +
                $"The server offers {DescribeEndpoints(schemeEndpoints)}; the client supports " +
                $"{string.Join(", ", supportedPolicies.Where(policy => policy != SecurityPolicies.None))}.");
        }

        return securedEndpoint;
    }

    /// <summary>
    /// Gets whether <see cref="CreateCertificateErrorException"/> explains a failed session creation: the client rejected
    /// the server certificate, or the server rejected the secure channel of a secured endpoint.
    /// </summary>
    internal static bool IsCertificateError(ServiceResultException error, EndpointDescription endpoint) =>
        error.StatusCode == StatusCodes.BadCertificateUntrusted ||
        error.StatusCode == StatusCodes.BadCertificateChainIncomplete ||
        error.StatusCode == StatusCodes.BadCertificateTimeInvalid ||
        (error.StatusCode == StatusCodes.BadSecurityChecksFailed && endpoint.SecurityMode != MessageSecurityMode.None);

    /// <summary>
    /// Creates the exception thrown for an error <see cref="IsCertificateError"/> accepts. The message names the
    /// certificate at fault and the folders or option that resolve it.
    /// </summary>
    /// <param name="error">The error the session creation failed with.</param>
    /// <param name="endpoint">The endpoint the session was created for.</param>
    /// <param name="applicationConfiguration">The client application configuration.</param>
    /// <param name="clientCertificate">The client certificate loaded from the own store for a secured endpoint, or <c>null</c>.</param>
    /// <param name="telemetryContext">The telemetry context.</param>
    internal static InvalidOperationException CreateCertificateErrorException(
        ServiceResultException error,
        EndpointDescription endpoint,
        ApplicationConfiguration applicationConfiguration,
        Certificate? clientCertificate,
        ITelemetryContext telemetryContext)
    {
        var security = applicationConfiguration.SecurityConfiguration;
        using var serverCertificate = ParseServerCertificate(endpoint, telemetryContext);
        var serverThumbprint = serverCertificate?.Thumbprint ?? "(unknown)";
        var ownFolder = CertificateFolder.Get(security.ApplicationCertificate?.StoreType, security.ApplicationCertificate?.StorePath);
        var rejectedFolder = CertificateFolder.Get(security.RejectedCertificateStore);
        var trustedFolder = CertificateFolder.Get(security.TrustedPeerCertificates);

        // A server also reports an expired client certificate with BadCertificateTimeInvalid, and one that is not yet
        // valid when its clock is behind, so both certificates can be valid by this machine's clock.
        var isClientCertificateSent = endpoint.SecurityMode != MessageSecurityMode.None && clientCertificate is not null;
        var isServerCertificateTimeValid = serverCertificate is not null && IsTimeValid(serverCertificate);
        var isClientCertificateTimeInvalid =
            isClientCertificateSent && !IsTimeValid(clientCertificate!) && (serverCertificate is null || isServerCertificateTimeValid);
        var areBothCertificatesTimeValid = isClientCertificateSent && IsTimeValid(clientCertificate!) && isServerCertificateTimeValid;

        // Auto-accept only covers an untrusted server certificate, so only that message suggests it.
        var statusCode = error.StatusCode;
        var message = true switch
        {
            // Servers report an untrusted, revoked or invalid client certificate, or one whose issuer chain is incomplete,
            // with this code. The client otherwise raises it only for a failed message signature or decryption, or a
            // mismatched endpoint list.
            _ when statusCode == StatusCodes.BadSecurityChecksFailed =>
                $"The OPC UA server rejected the secure channel, most likely because it does not trust this client's certificate " +
                $"{clientCertificate?.Thumbprint ?? "(unknown)"} in '{ownFolder}'. Trust it on the server; for a certificate issued " +
                $"by a certificate authority, the server also needs the authority's certificates.",
            _ when statusCode == StatusCodes.BadCertificateTimeInvalid && isClientCertificateTimeInvalid =>
                $"The OPC UA server rejected this client's certificate {clientCertificate!.Thumbprint} in '{ownFolder}' because it " +
                $"is expired or not yet valid{FormatValidity(clientCertificate)}. Provide a valid certificate there and trust it " +
                $"on the server, and check that the clocks of the server and this client are correct.",
            _ when statusCode == StatusCodes.BadCertificateTimeInvalid && areBothCertificatesTimeValid =>
                $"The OPC UA server rejected a certificate as expired or not yet valid, but this client's certificate " +
                $"{clientCertificate!.Thumbprint}{FormatValidity(clientCertificate)} and the server's certificate " +
                $"{serverThumbprint}{FormatValidity(serverCertificate)} are both valid by this machine's clock. " +
                $"Check that the clocks of the server and this client are correct.",
            _ when statusCode == StatusCodes.BadCertificateTimeInvalid =>
                $"The OPC UA server certificate {serverThumbprint} is expired or not yet valid{FormatValidity(serverCertificate)}. " +
                $"The server needs a valid certificate; also check that the clocks of the server and this client are correct.",
            _ when statusCode == StatusCodes.BadCertificateChainIncomplete =>
                $"The OPC UA server certificate {serverThumbprint} is not trusted because its issuer chain is incomplete. " +
                $"Add the certificates of the authorities that issued it to '{CertificateFolder.Get(security.TrustedIssuerCertificates)}' " +
                $"and the root authority's certificate to '{trustedFolder}'; the server's certificate chain is written to '{rejectedFolder}'.",
            _ =>
                $"The OPC UA server certificate {serverThumbprint} is not trusted. Move it from '{rejectedFolder}' to '{trustedFolder}', " +
                $"or set {nameof(OpcUaClientConfiguration.AutoAcceptUntrustedCertificates)} = true to accept any server certificate."
        };

        return new InvalidOperationException(message, error);
    }

    /// <summary>
    /// Creates the exception thrown when the client application certificate cannot be loaded, created or replaced.
    /// </summary>
    internal static InvalidOperationException CreateCertificateLoadException(
        Exception error,
        ApplicationConfiguration applicationConfiguration,
        string? subjectName,
        bool autoCreate)
    {
        var identifier = applicationConfiguration.SecurityConfiguration.ApplicationCertificate;
        return new InvalidOperationException(
            $"Failed to load the OPC UA client application certificate from " +
            $"'{CertificateFolder.Get(identifier?.StoreType, identifier?.StorePath)}': {error.Message.TrimEnd().TrimEnd('.')}. " +
            $"A provisioned certificate needs the subject '{subjectName}' and the application URI " +
            $"'{applicationConfiguration.ApplicationUri}'; set {nameof(OpcUaClientConfiguration.ApplicationUri)} to match it. " +
            (autoCreate
                ? $"Deleting the certificate with its private key from '{identifier?.StorePath}' makes the client create a new " +
                  "self-signed one, which the server then has to trust."
                : $"Or set {nameof(OpcUaClientConfiguration.AutoCreateApplicationCertificate)} = true to create one."),
            error);
    }

    private static string DescribeEndpoints(IEnumerable<EndpointDescription> endpoints) => string.Join(", ", endpoints
        .Select(endpoint => $"{endpoint.SecurityPolicyUri} ({endpoint.SecurityMode})")
        .Distinct(StringComparer.Ordinal));

    private static bool IsTimeValid(Certificate certificate)
    {
        var now = DateTime.UtcNow;
        return certificate.NotBefore.ToUniversalTime() <= now && now <= certificate.NotAfter.ToUniversalTime();
    }

    private static string FormatValidity(Certificate? certificate) => certificate is null
        ? string.Empty
        : $" (valid from {certificate.NotBefore.ToUniversalTime():u} to {certificate.NotAfter.ToUniversalTime():u})";

    // Called while wrapping an error, so it must not throw and hide that error.
    private static Certificate? ParseServerCertificate(EndpointDescription endpoint, ITelemetryContext telemetryContext)
    {
        if (endpoint.ServerCertificate.Length == 0)
        {
            return null;
        }

        try
        {
            // The server may send its whole chain; this parses the first certificate, which is its own.
            return Utils.ParseCertificateBlob(endpoint.ServerCertificate.Memory, telemetryContext);
        }
        catch (ServiceResultException)
        {
            return null;
        }
    }

    /// <summary>
    /// Closes the transport channel gracefully. The managed session detects the loss and reconnects.
    /// </summary>
    internal async Task DisconnectTransportAsync(CancellationToken cancellationToken)
    {
        var channel = CurrentSession?.NullableTransportChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await channel.CloseAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing transport channel, falling back to dispose.");
            KillTransport();
        }
    }

    /// <summary>
    /// Disposes the transport channel abruptly, so in-flight operations fail. The managed session
    /// detects the loss and reconnects.
    /// </summary>
    internal void KillTransport()
    {
        try
        {
            CurrentSession?.NullableTransportChannel?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error killing transport channel.");
        }
    }

    private async Task DisposeSessionAsync(ManagedSession session, CancellationToken cancellationToken)
    {
        session.ConnectionStateChanged -= OnConnectionStateChanged;
        session.SessionGenerationChanged -= OnSessionGenerationChanged;

        // Deleted with the session: nothing transfers them to a later session once this one closes.
        session.DeleteSubscriptionsOnClose = true;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_configuration.SessionDisposalTimeout);
            await session.CloseAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing OPC UA session (id={SessionId}).", session.SessionId);
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing OPC UA session.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_stateLock)
        {
            if (Interlocked.Exchange(ref _reconnectInProgress, 0) == 1)
            {
                _source.ReconnectionMetrics.RecordAbandoned();
            }
        }

        if (ReadAfterWriteManager is not null)
        {
            try { await ReadAfterWriteManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing read-after-write manager."); }
        }
        try { await SubscriptionManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing subscription manager."); }
        if (PollingManager is not null)
        {
            try { await PollingManager.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing polling manager."); }
        }

        // Cleared before the close, so no reader takes a closing session and the connector's value reads
        // and writes on it are canceled even when the close times out.
        var session = SetSession(null);
        DrainPendingSessionTransitions();
        if (session is not null)
        {
            using var cts = new CancellationTokenSource(_configuration.SessionDisposalTimeout);
            await DisposeSessionAsync(session, cts.Token).ConfigureAwait(false);
        }

        ReplaceClientCertificate(null);

        var application = Interlocked.Exchange(ref _application, null);
        if (application is not null)
        {
            try { await application.DisposeAsync().AsTask().WaitAsync(_configuration.SessionDisposalTimeout).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing OPC UA application instance."); }
        }
    }

    /// <summary>
    /// Fires <see cref="OpcUaSubjectClientSource.OnCurrentSessionChanged"/> for every pending session
    /// transition. Concurrent drainers serialize on <see cref="_drainLock"/>, preserving order.
    /// </summary>
    internal void DrainPendingSessionTransitions()
    {
        lock (_drainLock)
        {
            while (_pendingSessionTransitions.TryDequeue(out var transition))
            {
                _source.OnCurrentSessionChanged(transition.Previous, transition.Current);
            }
        }
    }

    /// <summary>
    /// Atomically replaces the current session, cancels the previous session's lifetime and enqueues a
    /// pending notification, which <see cref="DrainPendingSessionTransitions"/> fires. Setting the current
    /// session again changes nothing.
    /// </summary>
    /// <returns>The previous session.</returns>
    internal ManagedSession? SetSession(ManagedSession? newSession)
    {
        SessionLease? oldLease;
        SessionLease? newLease = null;
        do
        {
            oldLease = Volatile.Read(ref _sessionLease);
            if (ReferenceEquals(oldLease?.Session, newSession))
            {
                return oldLease?.Session;
            }

            newLease ??= newSession is null ? null : new SessionLease(newSession);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _sessionLease, newLease, oldLease), oldLease));

        oldLease?.End();
        _pendingSessionTransitions.Enqueue((oldLease?.Session, newSession));
        return oldLease?.Session;
    }

    /// <summary>
    /// A session with a token that is canceled once the session stops being current.
    /// </summary>
    private sealed class SessionLease
    {
        // Never disposed: it owns no timer or wait handle, and disposing it could drop the callbacks that
        // CancelAsync has not run yet.
        private readonly CancellationTokenSource _lifetime = new();

        public SessionLease(ManagedSession session)
        {
            Session = session;
            Lifetime = _lifetime.Token;
        }

        public ManagedSession Session { get; }

        public CancellationToken Lifetime { get; }

        /// <summary>
        /// Cancels <see cref="Lifetime"/>. The token reports cancellation on return, and its callbacks, which
        /// complete the requests observing it, run on the thread pool, so this is safe to call under a lock.
        /// </summary>
        public void End() => _ = _lifetime.CancelAsync();
    }

    /// <summary>
    /// Satisfies IDisposable for interface compatibility by delegating to <see cref="DisposeAsync"/>.
    /// </summary>
    void IDisposable.Dispose()
    {
        _logger.LogWarning("Sync Dispose() called on SessionManager - prefer DisposeAsync() for proper cleanup.");
        _ = Task.Run(async () =>
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during fire-and-forget disposal of SessionManager.");
            }
        });
    }
}
