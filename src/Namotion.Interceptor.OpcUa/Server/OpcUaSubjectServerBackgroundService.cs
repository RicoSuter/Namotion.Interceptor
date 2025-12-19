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

internal class OpcUaSubjectServerBackgroundService : BackgroundService
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly OpcUaServerConfiguration _configuration;

    private LifecycleInterceptor? _lifecycleInterceptor;
    private volatile OpcUaSubjectServer? _server;
    private int _consecutiveFailures;

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

    internal bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _configuration.PathProvider.IsPropertyIncluded(property);
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
            _lifecycleInterceptor.SubjectDetached += OnSubjectDetached;
        }

        try
        {
            await ExecuteServerLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            if (_lifecycleInterceptor is not null)
            {
                _lifecycleInterceptor.SubjectDetached -= OnSubjectDetached;
            }
        }
    }

    private async Task ExecuteServerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var application = _configuration.CreateApplicationInstance();

            if (_configuration.CleanCertificateStore)
            {
                CleanCertificateStore(application);
            }

            try
            {
                using var server = new OpcUaSubjectServer(_subject, this, _configuration, _logger);
                try
                {
                    _server = server;

                    await application.CheckApplicationInstanceCertificates(true);
                    await application.Start(server);

                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this, _context,
                        propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
                        _configuration.BufferTime, _logger);

                    await changeQueueProcessor.ProcessAsync(stoppingToken);
                    _consecutiveFailures = 0;
                }
                finally
                {
                    var serverToClean = _server;
                    _server = null;
                    serverToClean?.ClearPropertyData();
                }
            }
            catch (Exception ex)
            {
                if (ex is not TaskCanceledException)
                {
                    _consecutiveFailures++;
                    _logger.LogError(ex, "Failed to start OPC UA server (attempt {Attempt}).", _consecutiveFailures);

                    var delaySeconds = Math.Min(Math.Pow(2, _consecutiveFailures - 1), 30);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                }
            }
            finally
            {
                try
                {
                    ShutdownServer(application);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to shutdown OPC UA server.");
                }
            }
        }
    }

    private void ShutdownServer(ApplicationInstance application)
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

            application.Stop();
        }
        catch (ServiceResultException e) when (e.StatusCode == StatusCodes.BadServerHalted)
        {
            // Server already halted
        }
    }

    private static void CleanCertificateStore(ApplicationInstance application)
    {
        var path = application
            .ApplicationConfiguration
            .SecurityConfiguration
            .ApplicationCertificate
            .StorePath;

        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
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

    private void OnSubjectDetached(SubjectLifecycleChange change)
    {
        _server?.RemoveSubjectNodes(change.Subject);
    }
}
