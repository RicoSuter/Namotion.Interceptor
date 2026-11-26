using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenAddTeslaAccount_ThenConfigureIsAppliedAndTheSystemClockIsRegistered()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        // Act
        services.AddTeslaAccount(account =>
        {
            account.Name = "Garage";
            account.StatePollingInterval = TimeSpan.FromMinutes(2);
        });
        using var serviceProvider = services.BuildServiceProvider();

        // Assert
        var account = serviceProvider.GetRequiredService<TeslaAccount>();
        Assert.Equal("Garage", account.Name);
        Assert.Equal(TimeSpan.FromMinutes(2), account.StatePollingInterval);
        Assert.Same(TimeProvider.System, serviceProvider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void WhenATimeProviderIsRegistered_ThenAddTeslaAccountKeepsIt()
    {
        // Arrange
        var services = new ServiceCollection();
        var timeProvider = new FakeTimeProvider();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<TimeProvider>(timeProvider);

        // Act
        services.AddTeslaAccount();
        using var serviceProvider = services.BuildServiceProvider();

        // Assert
        Assert.Same(timeProvider, serviceProvider.GetRequiredService<TimeProvider>());
        Assert.Equal(TimeSpan.FromMinutes(1), serviceProvider.GetRequiredService<TeslaAccount>().StatePollingInterval);
    }
}
