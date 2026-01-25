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

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

public class OpcUaTestClient<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private const string DefaultServerUrl = "opc.tcp://localhost:4840";

    private readonly TestLogger _logger;
    private readonly Action<OpcUaClientConfiguration>? _configureClient;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;
    private int _disposed; // 0 = not disposed, 1 = disposed

    public TRoot? Root { get; private set; }

    public IInterceptorSubjectContext Context => _context ?? throw new InvalidOperationException("Client not started.");

    /// <summary>
    /// Gets the client diagnostics, or null if not started.
    /// </summary>
    public OpcUaClientDiagnostics? Diagnostics { get; private set; }

    public OpcUaTestClient(TestLogger logger, Action<OpcUaClientConfiguration>? configureClient = null)
    {
        _logger = logger;
        _configureClient = configureClient;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool> isConnected,
        string serverUrl = DefaultServerUrl,
        string? certificateStoreBasePath = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var builder = Host.CreateApplicationBuilder();

        // Reduce shutdown timeout for faster test cleanup
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(_logger, "Client", LogLevel.Information);
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

                var config = new OpcUaClientConfiguration
                {
                    ServerUrl = serverUrl,
                    RootName = "Root",
                    PathProvider = new AttributeBasedPathProvider("opc"),
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                    TelemetryContext = telemetryContext,
                    ReconnectInterval = TimeSpan.FromSeconds(5),
                    ReconnectHandlerTimeout = TimeSpan.FromSeconds(5),
                    SessionTimeout = TimeSpan.FromSeconds(5),
                    SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(5),
                    KeepAliveInterval = TimeSpan.FromSeconds(5),
                    OperationTimeout = TimeSpan.FromSeconds(5),
                    MaxReconnectDuration = TimeSpan.FromSeconds(15),
                    CertificateStoreBasePath = certificateStoreBasePath ?? "pki"
                };

                // Allow tests to override configuration
                _configureClient?.Invoke(config);

                return config;
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
        _logger.Log($"Client host started in {sw.ElapsedMilliseconds}ms");

        // First wait for OPC UA infrastructure (subscriptions set up) - this is reliable
        // because it's based on actual OPC UA state, not property propagation
        await AsyncTestHelpers.WaitUntilAsync(
            () => Diagnostics?.MonitoredItemCount > 0,
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(200),
            message: "Client failed to create subscriptions");

        // Then wait actual connected
        // WaitUntilAsync includes memory barrier to ensure visibility across threads
        await AsyncTestHelpers.WaitUntilAsync(
            () => Root != null && isConnected(Root),
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(200),
            message: "Client failed to sync initial property values");

        sw.Stop();
        _logger.Log($"Client connected in {sw.ElapsedMilliseconds}ms total");
    }

    public async Task StopAsync()
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                host.Dispose();
                Diagnostics = null;
            }
            sw.Stop();
            _logger.Log($"Client stopped in {sw.ElapsedMilliseconds}ms");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        try
        {
            var host = Interlocked.Exchange(ref _host, null);
            if (host != null)
            {
                await host.StopAsync(TimeSpan.FromSeconds(5));
                host.Dispose();
                _logger.Log("Client disposed");
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error disposing client: {ex.Message}");
        }
    }
}
