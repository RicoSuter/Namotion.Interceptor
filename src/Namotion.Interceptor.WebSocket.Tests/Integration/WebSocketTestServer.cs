using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

public class WebSocketTestServer<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    private Func<IInterceptorSubjectContext, TRoot>? _createRoot;
    private Action<IInterceptorSubjectContext, TRoot>? _initializeDefaults;
    private Action<WebSocketServerConfiguration>? _configureServer;
    private int _port;

    public TRoot? Root { get; private set; }
    public WebSocketSubjectServer? Server { get; private set; }

    public WebSocketTestServer(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Action<IInterceptorSubjectContext, TRoot>? initializeDefaults = null,
        int port = 18080,
        Action<WebSocketServerConfiguration>? configureServer = null)
    {
        _createRoot = createRoot;
        _initializeDefaults = initializeDefaults;
        _configureServer = configureServer;
        _port = port;

        await StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddConsole();
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithHostedServices(builder.Services);

        Root = _createRoot!(context);
        _initializeDefaults?.Invoke(context, Root);

        builder.Services.AddSingleton(Root);
        builder.Services.AddWebSocketSubjectServer<TRoot>(configuration =>
        {
            configuration.Port = _port;
            _configureServer?.Invoke(configuration);
        });

        _host = builder.Build();

        Server = _host.Services.GetServices<IHostedService>()
            .OfType<WebSocketSubjectServer>()
            .FirstOrDefault();

        await _host.StartAsync();

        // Wait for server to be ready
        await Task.Delay(500);
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartCoreAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
