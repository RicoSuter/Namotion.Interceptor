using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGpio_WithConfigure_AppliesConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddGpio(gpio =>
        {
            gpio.UseSimulation = true;
            gpio.PollingInterval = TimeSpan.FromSeconds(10);
        });
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var gpio = serviceProvider.GetRequiredService<GpioSubject>();
        Assert.True(gpio.UseSimulation);
        Assert.Equal(TimeSpan.FromSeconds(10), gpio.PollingInterval);
    }

    [Fact]
    public void AddGpio_WithoutConfigure_UsesDefaults()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddGpio();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var gpio = serviceProvider.GetRequiredService<GpioSubject>();
        Assert.False(gpio.UseSimulation);
        Assert.Equal(TimeSpan.FromSeconds(5), gpio.PollingInterval);
    }
}
