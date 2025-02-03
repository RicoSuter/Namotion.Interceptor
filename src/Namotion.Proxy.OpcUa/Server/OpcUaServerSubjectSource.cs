using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Proxy.Sources.Abstractions;
using Opc.Ua;
using Opc.Ua.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Proxy.OpcUa.Server;

internal class OpcUaServerSubjectSource<TProxy> : BackgroundService, ISubjectSource, IDisposable
    where TProxy : IInterceptorSubject
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly TProxy _proxy;
    private readonly ILogger _logger;
    private readonly string? _rootName;

    private ProxyOpcUaServer<TProxy>? _server;
    private Action<PropertyPathReference>? _propertyUpdateAction;

    internal ISourcePathProvider SourcePathProvider { get; }

    public OpcUaServerSubjectSource(
        TProxy proxy,
        ISourcePathProvider sourcePathProvider,
        ILogger<OpcUaServerSubjectSource<TProxy>> logger,
        string? rootName)
    {
        _proxy = proxy;
        _logger = logger;
        _rootName = rootName;

        SourcePathProvider = sourcePathProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _proxy.Context.WithRegistry();
        
        while (!stoppingToken.IsCancellationRequested)
        {
            using var stream = typeof(OpcUaProxyExtensions).Assembly
                .GetManifestResourceStream("Namotion.Proxy.OpcUa.MyOpcUaServer.Config.xml") ??
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
                _server = new ProxyOpcUaServer<TProxy>(_proxy, this, _rootName, _proxy.Context.GetService<ISubjectRegistry>());

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
