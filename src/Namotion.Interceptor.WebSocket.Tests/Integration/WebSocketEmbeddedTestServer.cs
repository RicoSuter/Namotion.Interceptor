using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Test server that uses AddWebSocketSubjectHandler and MapWebSocketSubjectHandler
/// to embed WebSocket handling in an ASP.NET Core application.
/// </summary>
public class WebSocketEmbeddedTestServer<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private WebApplication? _app;

    private Func<IInterceptorSubjectContext, TRoot>? _createRoot;
    private Action<IInterceptorSubjectContext, TRoot>? _initializeDefaults;
    private int _port;

    public TRoot? Root { get; private set; }
    public WebSocketSubjectHandler? Handler { get; private set; }

    public WebSocketEmbeddedTestServer(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Action<IInterceptorSubjectContext, TRoot>? initializeDefaults = null,
        int port = 18081)
    {
        _createRoot = createRoot;
        _initializeDefaults = initializeDefaults;
        _port = port;

        await StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        var builder = WebApplication.CreateBuilder();
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
        builder.Services.AddWebSocketSubjectHandler<TRoot>();

        builder.WebHost.UseUrls($"http://localhost:{_port}");

        _app = builder.Build();

        Handler = _app.Services.GetRequiredService<WebSocketSubjectHandler>();

        _app.UseWebSockets();
        _app.MapWebSocketSubjectHandler("/ws");

        await _app.StartAsync();

        // Wait for server to be ready
        await Task.Delay(500);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
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
