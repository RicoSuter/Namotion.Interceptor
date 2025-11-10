using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServerSource : BackgroundService, ISubjectSource
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly OpcUaServerConfiguration _configuration;

    private OpcUaSubjectServer? _server;
    private ISubjectUpdater? _updater;
    
    public OpcUaSubjectServerSource(
        IInterceptorSubject subject,
        OpcUaServerConfiguration configuration,
        ILogger<OpcUaSubjectServerSource> logger)
    {
        _subject = subject;
        _logger = logger;
        _configuration = configuration;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _configuration.SourcePathProvider.IsPropertyIncluded(property);
    }

    public Task<IDisposable?> StartListeningAsync(ISubjectUpdater updater, CancellationToken cancellationToken)
    {
        _updater = updater;
        return Task.FromResult<IDisposable?>(null);
    }

    public Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<Action?>(null);
    }

    public ValueTask WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var count = changes.Count;
        for (var i = 0; i < count; i++)
        {
            var change = changes[i];
            if (change.Property.TryGetPropertyData(OpcVariableKey, out var data) &&
                data is BaseDataVariableState node &&
                change.Property.TryGetRegisteredProperty() is { } registeredProperty)
            {
                var value = change.GetNewValue<object?>();
                var convertedValue = _configuration.ValueConverter
                    .ConvertToNodeValue(value, registeredProperty);

                node.Value = convertedValue;
                node.Timestamp = change.ChangedTimestamp.UtcDateTime;
                node.ClearChangeMasks(_server?.CurrentInstance.DefaultSystemContext, false);
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
                _logger.LogInformation("Cleaning up old certificates...");
            }
            try
            {
                _server = new OpcUaSubjectServer(_subject, this, _configuration);

                await application.CheckApplicationInstanceCertificates(true);
                await application.Start(_server);

                await Task.Delay(-1, stoppingToken);
            }
            catch (Exception ex)
            {
                if (ex is not TaskCanceledException)
                {
                    _logger.LogError(ex, "Failed to start OPC UA server.");
                }

                application.Stop();

                if (ex is not TaskCanceledException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
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
        var receivedTimestamp = DateTimeOffset.Now;

        var registeredProperty = property.TryGetRegisteredProperty();
        if (registeredProperty is not null)
        {
            var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(value, registeredProperty);

            var state = (source: this, property, changedTimestamp, receivedTimestamp, value: convertedValue);
            _updater?.EnqueueOrApplyUpdate(state,
                static s => s.property.SetValueFromSource(
                    s.source, s.changedTimestamp, s.receivedTimestamp, s.value));
        }
    }
}