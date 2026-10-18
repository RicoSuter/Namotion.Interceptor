using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenAddNukiBridge_ThenConfigureIsApplied()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();

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
    }
}
