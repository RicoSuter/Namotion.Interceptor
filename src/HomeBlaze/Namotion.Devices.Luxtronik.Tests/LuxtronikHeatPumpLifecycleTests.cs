using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
[Collection(LuxtronikIntegrationCollection.Name)]
public class LuxtronikHeatPumpLifecycleTests
{
    [Fact]
    public async Task WhenControllerStops_ThenHostedHeatPumpReportsError()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithHostedServices(services);
        await using var provider = services.BuildServiceProvider();
        var handler = Assert.Single(provider.GetServices<IHostedService>());
        await handler.StartAsync(CancellationToken.None);

        var heatPump = new LuxtronikHeatPump(NullLogger<LuxtronikHeatPump>.Instance)
        {
            HostAddress = "127.0.0.1",
            Port = server.Port,
            PollingInterval = TimeSpan.FromMilliseconds(200)
        };

        try
        {
            _ = new TestHost(context) { HeatPump = heatPump };
            await AsyncTestHelpers.WaitUntilAsync(
                () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running && heatPump.LastUpdated is not null,
                TimeSpan.FromSeconds(30),
                message: "The hosted heat pump should connect and poll.");

            // Act
            server.Stop();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => !heatPump.IsConnected && heatPump.Status == ServiceStatus.Error,
                TimeSpan.FromSeconds(30),
                message: "The heat pump should report the lost controller.");
        }
        finally
        {
            await handler.StopAsync(CancellationToken.None);
        }
    }
}
