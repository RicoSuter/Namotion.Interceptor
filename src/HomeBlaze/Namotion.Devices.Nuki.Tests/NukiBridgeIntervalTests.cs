using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeIntervalTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(0.5, NukiBridge.MinimumIntervalSeconds)]
    [InlineData(60, 60)]
    [InlineData(86400, NukiBridge.MaximumIntervalSeconds)]
    public void WhenAnIntervalIsConfigured_ThenItIsClampedOrFallsBackToTheFallback(double configuredSeconds, int expectedSeconds)
    {
        // Act
        var effective = NukiBridge.GetEffectiveInterval(TimeSpan.FromSeconds(configuredSeconds), TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), effective);
    }
}
