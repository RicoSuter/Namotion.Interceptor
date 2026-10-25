using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.SunSpec.Tests.Testing;

/// <summary>
/// Runs a <see cref="SunSpecDevice"/> as a hosted subject against a local Modbus server.
/// </summary>
internal sealed class HostedSunSpecDevice : IAsyncDisposable
{
    /// <summary>
    /// The polling interval of the hosted device, below the product minimum so a test observes several polls quickly.
    /// </summary>
    public static readonly TimeSpan TestPollingInterval = TimeSpan.FromMilliseconds(200);

    private readonly ServiceProvider _provider;
    private readonly IHostedService _handler;

    private HostedSunSpecDevice(ServiceProvider provider, IHostedService handler, SunSpecDevice device, IInterceptorSubjectContext context)
    {
        _provider = provider;
        _handler = handler;
        Device = device;
        Context = context;
    }

    public SunSpecDevice Device { get; }

    public IInterceptorSubjectContext Context { get; }

    public static async Task<HostedSunSpecDevice> StartAsync(int port, Action<SunSpecDevice>? configure = null, ILogger<SunSpecDevice>? logger = null)
    {
        var services = new ServiceCollection().AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithHostedServices(services);
        var provider = services.BuildServiceProvider();
        IHostedService handler;
        try
        {
            handler = Assert.Single(provider.GetServices<IHostedService>());
            await handler.StartAsync(CancellationToken.None);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }

        var device = new SunSpecDevice(logger ?? NullLogger<SunSpecDevice>.Instance)
        {
            HostAddress = "127.0.0.1",
            Port = port,
            MinimumPollingInterval = TestPollingInterval,
            PollingInterval = TestPollingInterval
        };
        configure?.Invoke(device);

        var host = new HostedSunSpecDevice(provider, handler, device, context);
        _ = new TestHost(context) { Device = device };
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        await _handler.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }
}
