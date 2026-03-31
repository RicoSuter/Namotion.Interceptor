using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Namotion.Devices.MyStrom.Tests;

public class MyStromServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenAddMyStromSwitch_ThenConfigureApplied()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();

        // Act
        services.AddMyStromSwitch(subject =>
        {
            subject.HostAddress = "192.168.1.x";
            subject.PollingInterval = TimeSpan.FromSeconds(10);
        });
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var subject = serviceProvider.GetRequiredService<MyStromSwitch>();
        Assert.Equal("192.168.1.x", subject.HostAddress);
        Assert.Equal(TimeSpan.FromSeconds(10), subject.PollingInterval);
    }

    [Fact]
    public void WhenAddMyStromSwitchWithoutConfigure_ThenUsesDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();

        // Act
        services.AddMyStromSwitch();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var subject = serviceProvider.GetRequiredService<MyStromSwitch>();
        Assert.Null(subject.HostAddress);
        Assert.Equal(TimeSpan.FromSeconds(15), subject.PollingInterval);
    }
}
