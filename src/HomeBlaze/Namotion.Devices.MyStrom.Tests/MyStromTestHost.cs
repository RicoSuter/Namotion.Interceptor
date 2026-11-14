using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.MyStrom.Tests;

/// <summary>
/// Runs a switch the way an application registers one, in a host whose HTTP clients send through the handler.
/// Disposing it stops the host, which stops the switch's service.
/// </summary>
internal sealed class MyStromTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private MyStromTestHost(IHost host)
    {
        _host = host;
    }

    public static async Task<MyStromTestHost> StartAsync(MyStromSwitch subject, HttpMessageHandler handler)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        builder.Services.AddSingleton(subject);
        builder.Services.AddSubject<MyStromSwitch>();

        var host = builder.Build();
        await host.StartAsync();
        return new MyStromTestHost(host);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
