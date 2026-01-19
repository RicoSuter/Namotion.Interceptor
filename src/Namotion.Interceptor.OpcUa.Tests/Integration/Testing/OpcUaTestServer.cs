using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

public class OpcUaTestServer<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private const string DefaultBaseAddress = "opc.tcp://localhost:4840/";

    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;
    private Func<IInterceptorSubjectContext, TRoot>? _createRoot;
    private Action<IInterceptorSubjectContext, TRoot>? _initializeDefaults;
    private string _baseAddress = DefaultBaseAddress;
    private string _certificateStoreBasePath = "pki";
    private int _disposed; // 0 = not disposed, 1 = disposed

    public TRoot? Root { get; private set; }

    /// <summary>
    /// Gets the server's base address.
    /// </summary>
    public string BaseAddress => _baseAddress;

    /// <summary>
    /// Gets the server diagnostics, or null if not started.
    /// </summary>
    public OpcUaServerDiagnostics? Diagnostics { get; private set; }

    public OpcUaTestServer(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Action<IInterceptorSubjectContext, TRoot>? initializeDefaults = null,
        string? baseAddress = null,
        string? certificateStoreBasePath = null)
    {
        _createRoot = createRoot;
        _initializeDefaults = initializeDefaults;
        _baseAddress = baseAddress ?? DefaultBaseAddress;
        _certificateStoreBasePath = certificateStoreBasePath ?? "pki";

        return StartInternalAsync();
    }

    private async Task StartInternalAsync()
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
            // Clear default providers to avoid EventLog disposal issues during test cleanup
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddConsole();
        });

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        Root = _createRoot!(_context);

        _initializeDefaults?.Invoke(_context, Root);

        builder.Services.AddSingleton(Root);
        builder.Services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaServerConfiguration
                {
                    RootName = "Root",
                    BaseAddress = _baseAddress,
                    PathProvider = new AttributeBasedPathProvider("opc"),
                    ValueConverter = new OpcUaValueConverter(),
                    TelemetryContext = telemetryContext,
                    // Keep certificates across restarts to allow client reconnection
                    CleanCertificateStore = false,
                    // Auto-accept client certificates in tests (matches client configuration)
                    AutoAcceptUntrustedCertificates = true,
                    // Use port-specific certificate store for parallel test isolation
                    CertificateStoreBasePath = _certificateStoreBasePath
                };
            });

        _host = builder.Build();

        // Get diagnostics from the server service
        var serverService = _host.Services
            .GetServices<IHostedService>()
            .OfType<OpcUaSubjectServerBackgroundService>()
            .FirstOrDefault();

        if (serverService != null)
        {
            Diagnostics = serverService.Diagnostics;
        }

        await _host.StartAsync();
        sw.Stop();
        _output.WriteLine($"Server started in {sw.ElapsedMilliseconds}ms");
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
            _output.WriteLine($"Server stopped in {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>
    /// Restarts the server (stop and start again with same configuration).
    /// </summary>
    public async Task RestartAsync()
    {
        // Reset disposed flag to allow restart
        Interlocked.Exchange(ref _disposed, 0);
        _output.WriteLine("Restarting server...");
        await StopAsync();
        await StartInternalAsync();
        _output.WriteLine("Server restarted");
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
                _output.WriteLine("Server host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing server: {ex.Message}");
        }
    }
}
