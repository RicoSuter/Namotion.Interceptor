using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;
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
    private Action<SubjectUpdate>? _applySourceChangeAction;

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

    public IInterceptorSubject Subject => _subject;
    
    public Task<IDisposable?> InitializeAsync(Action<SubjectUpdate> applySourceChangeAction, CancellationToken cancellationToken)
    {
        _applySourceChangeAction = applySourceChangeAction;
        return Task.FromResult<IDisposable?>(null);
    }

    public Task<SubjectUpdate> ReadFromSourceAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new SubjectUpdate());
    }

    public Task WriteToSourceAsync(SubjectUpdate update, CancellationToken cancellationToken)
    {
        foreach (var (_, _, property) in update
             .EnumeratePaths(_subject, SourcePathProvider))
        {
            if (property.Property.GetPropertyData(OpcVariableKey) is BaseDataVariableState node)
            {
                var actualValue = property.GetValue();
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subject.Context.WithRegistry();
        
        while (!stoppingToken.IsCancellationRequested)
        {
            using var stream = typeof(OpcUaSubjectServerSourceExtensions).Assembly
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
        // TODO: Implement actual correct conversion based on the property type
        var convertedValue = Convert.ChangeType(value, property.Metadata.Type);
        
        var update = _subject.CreateSubjectUpdateFromPath(sourcePath, convertedValue, SourcePathProvider);
        _applySourceChangeAction?.Invoke(update);
    }

    public string GetSourcePropertyPath(PropertyReference property)
    {
        return SourcePathProvider.GetPropertyPath(string.Empty, property.GetRegisteredProperty());
    }
}
