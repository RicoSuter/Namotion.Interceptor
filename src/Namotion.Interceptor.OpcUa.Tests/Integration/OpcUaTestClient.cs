using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

public class OpcUaTestClientConfiguration
{
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectHandlerTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SubscriptionHealthCheckInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int StallDetectionIterations { get; init; } = 10;
}

public class OpcUaTestClient<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private const string DefaultServerUrl = "opc.tcp://localhost:4840";

    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;

    public TRoot? Root { get; private set; }

    public IInterceptorSubjectContext Context => _context ?? throw new InvalidOperationException("Client not started.");

    /// <summary>
    /// Gets the client diagnostics, or null if not started.
    /// </summary>
    public OpcUaClientDiagnostics? Diagnostics { get; private set; }

    public OpcUaTestClient(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool> isConnected,
        string serverUrl = DefaultServerUrl)
    {
        return StartAsync(createRoot, isConnected, new OpcUaTestClientConfiguration(), serverUrl);
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool> isConnected,
        OpcUaTestClientConfiguration configuration,
        string serverUrl = DefaultServerUrl)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddConsole();
        });

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithSourceTransactions()
            .WithHostedServices(builder.Services);

        Root = createRoot(_context);

        builder.Services.AddSingleton(Root);
        builder.Services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<TRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaClientConfiguration
                {
                    ServerUrl = serverUrl,
                    RootName = "Root",
                    PathProvider = new AttributeBasedPathProvider("opc"),
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                    TelemetryContext = telemetryContext,
                    ReconnectInterval = configuration.ReconnectInterval,
                    ReconnectHandlerTimeout = configuration.ReconnectHandlerTimeout,
                    SessionTimeout = configuration.SessionTimeout,
                    SubscriptionHealthCheckInterval = configuration.SubscriptionHealthCheckInterval,
                    KeepAliveInterval = configuration.KeepAliveInterval,
                    OperationTimeout = configuration.OperationTimeout,
                    StallDetectionIterations = configuration.StallDetectionIterations
                };
            });

        _host = builder.Build();

        // Get diagnostics from the client source
        var clientSource = _host.Services
            .GetServices<IHostedService>()
            .OfType<OpcUaSubjectClientSource>()
            .FirstOrDefault();

        if (clientSource != null)
        {
            Diagnostics = clientSource.Diagnostics;
        }

        await _host.StartAsync();
        _output.WriteLine("Client started");

        // Wait for client to connect using active waiting
        await AsyncTestHelpers.WaitUntilAsync(
            () => Root != null && isConnected(Root),
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(200),
            message: "Client failed to connect to server");

        _output.WriteLine("Client connected");
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
            Diagnostics = null;
            _output.WriteLine("Client stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host != null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
                _output.WriteLine("Client host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing client: {ex.Message}");
        }
    }
}
