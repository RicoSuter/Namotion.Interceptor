using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenAddSonos_ThenConfigureIsApplied()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddLogging();

        // Act
        services.AddSonos(system => system.SeedHost = "10.0.0.121");
        using var serviceProvider = services.BuildServiceProvider();

        // Assert
        var system = serviceProvider.GetRequiredService<SonosSystem>();
        Assert.Equal("10.0.0.121", system.SeedHost);
        Assert.Equal(6329, system.EventPort);
    }
}
