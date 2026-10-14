using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeIntervalTests
{
    [Theory]
    [InlineData(0, HueBridge.DefaultPollingIntervalSeconds, HueBridge.DefaultPollingIntervalSeconds)]
    [InlineData(0, HueBridge.DefaultRetryIntervalSeconds, HueBridge.DefaultRetryIntervalSeconds)]
    [InlineData(-10, HueBridge.DefaultPollingIntervalSeconds, HueBridge.DefaultPollingIntervalSeconds)]
    [InlineData(0.5, HueBridge.DefaultPollingIntervalSeconds, HueBridge.MinimumIntervalSeconds)]
    [InlineData(5, HueBridge.DefaultPollingIntervalSeconds, HueBridge.MinimumIntervalSeconds)]
    [InlineData(60, HueBridge.DefaultPollingIntervalSeconds, 60)]
    [InlineData(3600, HueBridge.DefaultPollingIntervalSeconds, HueBridge.MaximumIntervalSeconds)]
    [InlineData(86400, HueBridge.DefaultPollingIntervalSeconds, HueBridge.MaximumIntervalSeconds)]
    public void WhenAnIntervalIsConfigured_ThenItIsClampedOrFallsBackToTheDefault(double configuredSeconds, int defaultSeconds, int expectedSeconds)
    {
        // Act
        var effective = HueBridge.GetEffectiveInterval(TimeSpan.FromSeconds(configuredSeconds), defaultSeconds);

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
