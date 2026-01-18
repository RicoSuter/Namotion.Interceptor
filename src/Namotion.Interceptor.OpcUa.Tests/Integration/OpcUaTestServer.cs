using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

public class OpcUaTestServer<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;
    private Func<IInterceptorSubjectContext, TRoot>? _createRoot;
    private Action<IInterceptorSubjectContext, TRoot>? _initializeDefaults;

    public TRoot? Root { get; private set; }

    /// <summary>
    /// Gets the server diagnostics, or null if not started.
    /// </summary>
    public OpcUaServerDiagnostics? Diagnostics { get; private set; }

    public OpcUaTestServer(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Action<IInterceptorSubjectContext, TRoot>? initializeDefaults = null)
    {
        _createRoot = createRoot;
        _initializeDefaults = initializeDefaults;

        await StartInternalAsync();
    }

    private async Task StartInternalAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
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
        builder.Services.AddOpcUaSubjectServer<TRoot>("opc", rootName: "Root");

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
        _output.WriteLine("Server started");
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
            Diagnostics = null;
            _output.WriteLine("Server stopped");
        }
    }

    /// <summary>
    /// Restarts the server (stop and start again with same configuration).
    /// </summary>
    public async Task RestartAsync()
    {
        _output.WriteLine("Restarting server...");
        await StopAsync();
        await StartInternalAsync();
        _output.WriteLine("Server restarted");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host != null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
                _output.WriteLine("Server host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing server: {ex.Message}");
        }
    }
}
