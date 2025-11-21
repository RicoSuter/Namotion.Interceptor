using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaServerConnector : BackgroundService, ISubjectDownstreamConnector
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly OpcUaServerConfiguration _configuration;

    private volatile OpcUaSubjectServer? _server;
    private volatile ConnectorUpdateBuffer? _updateBuffer;
    private int _consecutiveFailures;

    public OpcUaServerConnector(
        IInterceptorSubject subject,
        OpcUaServerConfiguration configuration,
        ILogger<OpcUaServerConnector> logger)
    {
        _subject = subject;
        _logger = logger;
        _configuration = configuration;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _configuration.PathProvider.IsPropertyIncluded(property);
    }

    public Task<IDisposable?> StartListeningAsync(ConnectorUpdateBuffer updateBuffer, CancellationToken cancellationToken)
    {
        _updateBuffer = updateBuffer;
        return Task.FromResult<IDisposable?>(null);
    }

    public int WriteBatchSize => int.MaxValue;

    public ValueTask WriteToSourceAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
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
        _subject.Context.WithRegistry();

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

                    await Task.Delay(-1, stoppingToken);

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

            var state = (source: this, property, changedTimestamp, receivedTimestamp, value: convertedValue);
            _updateBuffer?.ApplyUpdate(state,
                static s => s.property.SetValueFromSource(
                    s.source, s.changedTimestamp, s.receivedTimestamp, s.value));
        }
    }
}
