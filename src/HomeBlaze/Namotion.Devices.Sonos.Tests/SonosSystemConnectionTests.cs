using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemConnectionTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(-10, 30)]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(600, 600)]
    [InlineData(3600, 3600)]
    [InlineData(7200, 3600)]
    public void WhenIntervalIsConfigured_ThenItIsClampedOrFallsBackToTheDefault(int configuredSeconds, int expectedSeconds)
    {
        // Act
        var interval = SonosSystem.GetEffectiveInterval(TimeSpan.FromSeconds(configuredSeconds), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Fact]
    public void WhenIntervalIsTheLargestTimeSpan_ThenItIsClampedToOneHour()
    {
        // Act
        var interval = SonosSystem.GetEffectiveInterval(TimeSpan.MaxValue, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(TimeSpan.FromHours(1), interval);
    }
}
