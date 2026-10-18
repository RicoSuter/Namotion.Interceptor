using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Namotion.Devices.Pushover.Tests;

public class PushoverServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenAddPushoverService_ThenConfigureIsApplied()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddLogging();

        // Act
        services.AddPushoverService(service => service.ApplicationToken = "app-token");
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var service = serviceProvider.GetRequiredService<PushoverService>();
        Assert.Equal("app-token", service.ApplicationToken);
        Assert.Empty(service.Targets);
    }
}
