using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

public class WebSocketTestClient<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public TRoot? Root { get; private set; }

    public WebSocketTestClient(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool>? isConnected = null,
        int port = 18080)
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

        Root = createRoot(context);

        builder.Services.AddSingleton(Root);
        builder.Services.AddWebSocketSubjectClientSource<TRoot>(configuration =>
        {
            configuration.ServerUri = new Uri($"ws://localhost:{port}/ws");
        });

        _host = builder.Build();
        await _host.StartAsync();

        // Wait for connection and initial sync (if caller provides a predicate)
        if (isConnected != null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!isConnected(Root) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
