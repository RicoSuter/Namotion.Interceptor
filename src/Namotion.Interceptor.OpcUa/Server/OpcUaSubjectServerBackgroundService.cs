using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServerBackgroundService : BackgroundService, ISubjectConnector, IFaultInjectable
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly OpcUaServerConfiguration _configuration;

    private LifecycleInterceptor? _lifecycleInterceptor;
    private volatile OpcUaSubjectServer? _server;
    private volatile bool _isForceKill;
    private volatile CancellationTokenSource? _forceKillCts;
    private int _consecutiveFailures;
    private OpcUaServerDiagnostics? _diagnostics;
    private DateTimeOffset? _startTime;
    private Exception? _lastError;

    /// <inheritdoc />
    public IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        // For a multi-connection server, all fault types are treated as force-kill.
        // There's no meaningful "soft disconnect" when the server has multiple clients.
        _isForceKill = true;
        try { _forceKillCts?.Cancel(); }
        catch (ObjectDisposedException) { /* CTS disposed between loop iterations */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets diagnostic information about the server state.
    /// </summary>
    public OpcUaServerDiagnostics Diagnostics => _diagnostics ??= new OpcUaServerDiagnostics(this);

    /// <summary>
    /// Gets a value indicating whether the server is running.
    /// </summary>
    internal bool IsRunning => _server?.CurrentInstance != null;

    /// <summary>
    /// Gets the number of active sessions.
    /// </summary>
    internal int ActiveSessionCount => _server?.CurrentInstance?.SessionManager?.GetSessions()?.Count ?? 0;

    /// <summary>
    /// Gets the server start time.
    /// </summary>
    internal DateTimeOffset? StartTime => _startTime;

    /// <summary>
    /// Gets the last error.
    /// </summary>
    internal Exception? LastError => _lastError;

    /// <summary>
    /// Gets the consecutive failure count.
    /// </summary>
    internal int ConsecutiveFailures => _consecutiveFailures;

    public OpcUaSubjectServerBackgroundService(
        IInterceptorSubject subject,
        OpcUaServerConfiguration configuration,
        ILogger logger)
    {
        _subject = subject;
        _context = subject.Context;
        _logger = logger;
        _configuration = configuration;
    }

    private bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return property.IsPropertyIncluded(_configuration.NodeMapper);
    }

    private ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var server = _server;
        var currentInstance = server?.CurrentInstance;
        if (currentInstance == null)
        {
            return ValueTask.CompletedTask;
        }

        // Use the SDK's NodeManager.Lock for thread-safe node updates.
        // This is the same lock the SDK uses for Read/Write/subscription operations.
        // ClearChangeMasks → OnMonitoredNodeChanged also acquires this lock,
        // but Monitor is reentrant on the same thread so no deadlock.
        var nodeManagerLock = server?.NodeManagerLock;
        if (nodeManagerLock == null)
        {
            return ValueTask.CompletedTask;
        }

        var span = changes.Span;
        lock (nodeManagerLock)
        {
            for (var i = 0; i < span.Length; i++)
            {
                var change = span[i];
                if (change.Property.TryGetPropertyData(OpcVariableKey, out var data) &&
                    data is BaseDataVariableState node &&
                    change.Property.TryGetRegisteredProperty() is { } registeredProperty)
                {
                    var value = change.GetNewValue<object?>();
                    var convertedValue = _configuration.ValueConverter
                        .ConvertToNodeValue(value, registeredProperty);

                    node.Value = convertedValue;
                    node.Timestamp = change.ChangedTimestamp.UtcDateTime;
                    node.ClearChangeMasks(currentInstance.DefaultSystemContext, false);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _context.WithRegistry();

        _lifecycleInterceptor = _context.TryGetLifecycleInterceptor();
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
        }

        try
        {
            await ExecuteServerLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            if (_lifecycleInterceptor is not null)
            {
                _lifecycleInterceptor.SubjectDetaching -= OnSubjectDetaching;
            }
        }
    }

    private async Task ExecuteServerLoopAsync(CancellationToken stoppingToken)
    {
        // Reset failure counter on fresh start so that accumulated failures from
        // previous stop/start cycles don't cause excessive backoff delays.
        _consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _forceKillCts = cts;
            var linkedToken = cts.Token;

            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);

            if (_configuration.CleanCertificateStore)
            {
                CleanCertificateStore(application);
            }

            var server = new OpcUaSubjectServer(_subject, this, _configuration, _logger);
            try
            {
                try
                {
                    _server = server;

                    // Create the ChangeQueueProcessor (and its subscription) BEFORE starting the server.
                    // This ensures property changes during OPC UA node creation are captured in the queue
                    // and not lost in the gap between node creation and processing start.
                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this, _context,
                        propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
                        _configuration.BufferTime, _logger);

                    await application.CheckApplicationInstanceCertificatesAsync(true, ct: linkedToken).ConfigureAwait(false);
                    await application.StartAsync(server).ConfigureAwait(false);

                    _startTime = DateTimeOffset.UtcNow;
                    _consecutiveFailures = 0;
                    _lastError = null;

                    await changeQueueProcessor.ProcessAsync(linkedToken);
                }
                finally
                {
                    _startTime = null;
                    var serverToClean = _server;
                    _server = null;
                    serverToClean?.ClearPropertyData();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown takes priority over force-kill (checked first intentionally).
                // If both stoppingToken and _isForceKill are set, we exit cleanly rather than restart.
            }
            catch (OperationCanceledException) when (_isForceKill)
            {
                // Force-kill: CTS was cancelled by KillAsync
                _logger.LogWarning("OPC UA server force-killed. Restarting...");
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _lastError = ex;
                _logger.LogError(ex, "Failed to start OPC UA server (attempt {Attempt}).", _consecutiveFailures);

                // Exponential backoff with jitter: 1s, 2s, 4s, 8s, 16s, 30s (capped) + 0-2s random jitter
                // Jitter prevents thundering herd when multiple servers fail simultaneously
                var baseDelay = Math.Min(Math.Pow(2, _consecutiveFailures - 1), 30);
                var jitter = Random.Shared.NextDouble() * 2;
                await Task.Delay(TimeSpan.FromSeconds(baseDelay + jitter), stoppingToken);
            }
            finally
            {
                try
                {
                    if (_isForceKill)
                    {
                        // Force-kill: close transport listeners immediately so clients see
                        // an abrupt connection loss (realistic crash simulation).
                        if (application.Server is OpcUaSubjectServer s)
                        {
                            s.CloseTransportListeners();
                        }
                    }

                    // Always run ShutdownServerAsync to ensure the SDK's internal tasks
                    // (SubscriptionManager publish/refresh threads) are properly signaled
                    // to exit via OnServerStoppingAsync. Without StopAsync, these
                    // fire-and-forget tasks keep the entire server object graph alive as
                    // GC roots, causing ~8-16 MB leak per server restart.
                    // On force-kill the transport is already dead, so this only cleans up
                    // internal state — it doesn't change what clients observe.
                    await ShutdownServerAsync(application).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to shutdown OPC UA server.");
                }
                finally
                {
                    _isForceKill = false;

                    // Dispose transport listeners before server.Dispose().
                    // The SDK's StopAsync calls Close() on each listener (which only
                    // stops accepting connections) then clears the TransportListeners list.
                    // When server.Dispose() later tries to Dispose() listeners, the list
                    // is empty — so TcpTransportListener.Dispose() never runs, leaving
                    // per-client channel sockets and inactivity timers alive as GC roots
                    // that retain the entire server object graph.
                    try { server.DisposeTransportListeners(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Error disposing transport listeners."); }

                    try { server.Dispose(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Error disposing OPC UA server."); }

                    cts.Dispose();
                }
            }
        }
    }

    private async Task ShutdownServerAsync(ApplicationInstance application)
    {
        try
        {
            if (application.Server is OpcUaSubjectServer server)
            {
                // Close transport listeners first to stop accepting new connections.
                // Without this, clients reconnect during shutdown faster than sessions
                // can be closed, causing StopAsync to hang indefinitely.
                server.CloseTransportListeners();

                if (server.CurrentInstance?.SessionManager is { } sessionManager)
                {
                    var sessions = sessionManager.GetSessions();
                    foreach (var session in sessions)
                    {
                        try { session.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "Error closing session during shutdown."); }
                    }
                }
            }

            // Timeout prevents hang when clients keep reconnecting during shutdown
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await application.StopAsync().AsTask().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("OPC UA server shutdown timed out after 10s. Continuing with disposal.");
            }
        }
        catch (ServiceResultException e) when (e.StatusCode == StatusCodes.BadServerHalted)
        {
            // Server already halted
        }
    }

    private void CleanCertificateStore(ApplicationInstance application)
    {
        var path = application
            .ApplicationConfiguration
            .SecurityConfiguration
            .ApplicationCertificate
            .StorePath;

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("Certificate store path is empty, skipping cleanup.");
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                _logger.LogDebug("Cleaned certificate store at {Path}.", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean certificate store at {Path}. Continuing with existing certificates.", path);
        }
    }

    internal void UpdateProperty(PropertyReference property, DateTimeOffset changedTimestamp, object? value)
    {
        var receivedTimestamp = DateTimeOffset.UtcNow;

        var registeredProperty = property.TryGetRegisteredProperty();
        if (registeredProperty is not null)
        {
            var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(value, registeredProperty);

            try
            {
                property.SetValueFromSource(this, changedTimestamp, receivedTimestamp, convertedValue);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to apply property update from OPC UA client.");
            }
        }
    }

    private void OnSubjectDetaching(SubjectLifecycleChange change)
    {
        _server?.RemoveSubjectNodes(change.Subject);
    }
}
