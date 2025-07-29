using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServerSource : BackgroundService, ISubjectSource
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly string? _rootName;

    private OpcUaSubjectServer? _server;
    private ISubjectMutationDispatcher? _dispatcher;

    internal ISourcePathProvider SourcePathProvider { get; }

    public OpcUaSubjectServerSource(
        IInterceptorSubject subject,
        ISourcePathProvider sourcePathProvider,
        ILogger<OpcUaSubjectServerSource> logger,
        string? rootName)
    {
        _subject = subject;
        _logger = logger;
        _rootName = rootName;

        SourcePathProvider = sourcePathProvider;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return SourcePathProvider.IsPropertyIncluded(property);
    }

    public Task<IDisposable?> StartListeningAsync(ISubjectMutationDispatcher dispatcher, CancellationToken cancellationToken)
    {
        _dispatcher = dispatcher;
        return Task.FromResult<IDisposable?>(null);
    }

    public Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<Action?>(null);
    }

    public Task WriteToSourceAsync(IEnumerable<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            if (change.Property.TryGetPropertyData(OpcVariableKey, out var data) && 
                data is BaseDataVariableState node)
            {
                var actualValue = change.Property.GetValue();
                if (actualValue is decimal)
                {
                    actualValue = Convert.ToDouble(actualValue);
                }

                node.Value = actualValue;
                node.Timestamp = change.Timestamp.UtcDateTime;
                node.ClearChangeMasks(_server?.CurrentInstance.DefaultSystemContext, false);
            }
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subject.Context.WithRegistry();

        while (!stoppingToken.IsCancellationRequested)
        {
            using var stream = typeof(OpcUaSubjectServerExtensions).Assembly
                .GetManifestResourceStream("Namotion.Interceptor.OpcUa.MyOpcUaServer.Config.xml")
                ?? throw new InvalidOperationException("Config.xml not found.");

            var application = new ApplicationInstance
            {
                ApplicationName = "MyOpcUaServer",
                ApplicationType = ApplicationType.Server,
                ApplicationConfiguration = await ApplicationConfiguration.Load(
                    stream, ApplicationType.Server, typeof(ApplicationConfiguration), false)
            };

            try
            {
                _server = new OpcUaSubjectServer(_subject, this, _rootName);

                await application.CheckApplicationInstanceCertificate(true, CertificateFactory.DefaultKeySize);
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

    internal void UpdateProperty(PropertyReference property, DateTimeOffset timestamp, object? value)
    {
        // TODO: Implement actual correct conversion based on the property type
        var convertedValue = Convert.ChangeType(value, property.Metadata.Type);
        
        _dispatcher?.EnqueueSubjectUpdate(() =>
        {
            property.SetValueFromSource(this, timestamp, convertedValue);
        });
    }
}