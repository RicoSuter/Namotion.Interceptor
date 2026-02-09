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

internal class OpcUaSubjectServerBackgroundService : BackgroundService, ISubjectConnector
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly OpcUaServerConfiguration _configuration;

    private LifecycleInterceptor? _lifecycleInterceptor;
    private volatile OpcUaSubjectServer? _server;
    private int _consecutiveFailures;
    private OpcUaServerDiagnostics? _diagnostics;
    private DateTimeOffset? _startTime;
    private Exception? _lastError;

    /// <inheritdoc />
    public IInterceptorSubject RootSubject => _subject;

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
        var currentInstance = _server?.CurrentInstance;
        if (currentInstance == null)
        {
            return ValueTask.CompletedTask;
        }

        var span = changes.Span;
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

                lock (node)
                {
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

                    await application.CheckApplicationInstanceCertificatesAsync(true, ct: stoppingToken).ConfigureAwait(false);
                    await application.StartAsync(server).ConfigureAwait(false);

                    _startTime = DateTimeOffset.UtcNow;
                    _consecutiveFailures = 0;
                    _lastError = null;

                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this, _context,
                        propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
                        _configuration.BufferTime, _logger);

                    await changeQueueProcessor.ProcessAsync(stoppingToken);
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
                // Normal shutdown - don't log as error
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
                    // ShutdownServerAsync must run BEFORE server.Dispose() so that
                    // application.StopAsync() can properly release the TCP listener socket.
                    // If the server is disposed first, the application can't cleanly shut down
                    // the transport layer, causing the TCP port to remain held.
                    await ShutdownServerAsync(application).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to shutdown OPC UA server.");
                }
                finally
                {
                    server.Dispose();
                }
            }
        }
    }

    private async Task ShutdownServerAsync(ApplicationInstance application)
    {
        try
        {
            if (application.Server is OpcUaSubjectServer { CurrentInstance.SessionManager: not null } server)
            {
                var sessions = server.CurrentInstance.SessionManager.GetSessions();
                foreach (var session in sessions)
                {
                    session.Close();
                }
            }

            await application.StopAsync().ConfigureAwait(false);
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
