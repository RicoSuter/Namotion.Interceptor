using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenAddNukiBridge_ThenTheConfiguredBridgeIsRegisteredAsSingletonAndHostedService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Act
        services.AddNukiBridge(bridge =>
        {
            bridge.HostAddress = "192.168.1.50";
            bridge.TokenMode = NukiTokenMode.Plain;
        });
        using var serviceProvider = services.BuildServiceProvider();

        // Assert
        var bridge = serviceProvider.GetRequiredService<NukiBridge>();
        Assert.Equal("192.168.1.50", bridge.HostAddress);
        Assert.Equal(NukiTokenMode.Plain, bridge.TokenMode);
        Assert.Equal(NukiBridge.DefaultPollingInterval, bridge.PollingInterval);
        Assert.Same(bridge, serviceProvider.GetRequiredService<NukiBridge>());
        Assert.Same(bridge, Assert.Single(serviceProvider.GetServices<IHostedService>()));
    }
}
