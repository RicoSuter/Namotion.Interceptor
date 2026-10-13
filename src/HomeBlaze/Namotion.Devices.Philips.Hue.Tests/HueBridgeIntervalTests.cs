using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeIntervalTests
{
    [Theory]
    [InlineData(0, HueBridge.DefaultPollingIntervalSeconds)]
    [InlineData(-10, HueBridge.DefaultPollingIntervalSeconds)]
    [InlineData(0.5, HueBridge.MinimumIntervalSeconds)]
    [InlineData(60, 60)]
    [InlineData(86400, HueBridge.MaximumIntervalSeconds)]
    public void WhenAnIntervalIsConfigured_ThenItIsClampedOrFallsBackToTheDefault(double configuredSeconds, int expectedSeconds)
    {
        // Act
        var effective = HueBridge.GetEffectiveInterval(TimeSpan.FromSeconds(configuredSeconds), HueBridge.DefaultPollingIntervalSeconds);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), effective);
    }

    [Fact]
    public void WhenABridgeIsCreated_ThenItsIntervalsAreTheDefaults()
    {
        // Act
        var bridge = TestHelpers.CreateTestBridge();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(HueBridge.DefaultPollingIntervalSeconds), bridge.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(HueBridge.DefaultRetryIntervalSeconds), bridge.RetryInterval);
    }
}
