using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServerSource<TSubject> : BackgroundService, ISubjectSource, IDisposable
    where TSubject : IInterceptorSubject
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly TSubject _subject;
    private readonly ILogger _logger;
    private readonly string? _rootName;

    private OpcUaSubjectServer<TSubject>? _server;
    private Action<PropertyPathReference>? _propertyUpdateAction;

    internal ISourcePathProvider SourcePathProvider { get; }

    public OpcUaSubjectServerSource(
        TSubject subject,
        ISourcePathProvider sourcePathProvider,
        ILogger<OpcUaSubjectServerSource<TSubject>> logger,
        string? rootName)
    {
        _subject = subject;
        _logger = logger;
        _rootName = rootName;

        SourcePathProvider = sourcePathProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subject.Context.WithRegistry();
        
        while (!stoppingToken.IsCancellationRequested)
        {
            using var stream = typeof(OpcUaSubjectExtensions).Assembly
                .GetManifestResourceStream("Namotion.Interceptor.OpcUa.MyOpcUaServer.Config.xml") ??
                throw new InvalidOperationException("Config.xml not found.");

            var application = new ApplicationInstance
            {
                ApplicationName = "MyOpcUaServer",
                ApplicationType = ApplicationType.Server,
                ApplicationConfiguration = await ApplicationConfiguration.Load(
                    stream, ApplicationType.Server, typeof(ApplicationConfiguration), false)
            };

            try
            {
                _server = new OpcUaSubjectServer<TSubject>(_subject, this, _rootName);

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

    internal void UpdateProperty(PropertyReference property, string sourcePath, object? value)
    {
        var convertedValue = Convert.ChangeType(value, property.Metadata.Type); // TODO: improve conversion here
        _propertyUpdateAction?.Invoke(new PropertyPathReference(property, sourcePath, convertedValue));
    }

    public Task<IDisposable?> InitializeAsync(IEnumerable<PropertyPathReference> properties, Action<PropertyPathReference> propertyUpdateAction, CancellationToken cancellationToken)
    {
        _propertyUpdateAction = propertyUpdateAction;
        return Task.FromResult<IDisposable?>(null);
    }

    public Task<IEnumerable<PropertyPathReference>> ReadAsync(IEnumerable<PropertyPathReference> properties, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<PropertyPathReference>>(properties
            .Where(p => p.Property.TryGetPropertyData(OpcVariableKey, out _))
            .Select(property => (property, node: property.Property.GetPropertyData(OpcVariableKey) as BaseDataVariableState))
            .Where(p => p.node is not null)
            .Select(p => new PropertyPathReference(p.property.Property, p.property.Path,
                p.property.Property.Metadata.Type == typeof(decimal) ? Convert.ToDecimal(p.node!.Value) : p.node!.Value))
            .ToList());
    }

    public Task WriteAsync(IEnumerable<PropertyPathReference> propertyChanges, CancellationToken cancellationToken)
    {
        foreach (var property in propertyChanges
            .Where(p => p.Property.TryGetPropertyData(OpcVariableKey, out var _)))
        {
            if (property.Property.GetPropertyData(OpcVariableKey) is BaseDataVariableState node)
            {
                var actualValue = property.Value;
                if (actualValue is decimal)
                {
                    actualValue = Convert.ToDouble(actualValue);
                }

                node.Value = actualValue;
                node.ClearChangeMasks(_server?.CurrentInstance.DefaultSystemContext, false);
            }
        }

        return Task.CompletedTask;
    }

    public string? TryGetSourcePath(PropertyReference property)
    {
        return SourcePathProvider.TryGetSourcePath(property);
    }
}
