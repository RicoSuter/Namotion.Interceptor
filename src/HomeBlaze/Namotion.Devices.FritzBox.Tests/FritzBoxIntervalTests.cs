using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxIntervalTests
{
    [Theory]
    [InlineData(0, FritzBox.DefaultPollingIntervalSeconds)]
    [InlineData(-10, FritzBox.DefaultPollingIntervalSeconds)]
    [InlineData(0.5, FritzBox.MinimumIntervalSeconds)]
    [InlineData(FritzBox.MinimumIntervalSeconds, FritzBox.MinimumIntervalSeconds)]
    [InlineData(60, 60)]
    [InlineData(FritzBox.MaximumIntervalSeconds, FritzBox.MaximumIntervalSeconds)]
    [InlineData(86400, FritzBox.MaximumIntervalSeconds)]
    [InlineData(0, FritzBox.DefaultRetryIntervalSeconds, FritzBox.DefaultRetryIntervalSeconds)]
    public void WhenAnIntervalIsConfigured_ThenItIsClampedOrFallsBackToTheDefault(
        double configuredSeconds, int expectedSeconds, int defaultSeconds = FritzBox.DefaultPollingIntervalSeconds)
    {
        // Act
        var effective = FritzBox.GetEffectiveInterval(
            TimeSpan.FromSeconds(configuredSeconds),
            defaultSeconds,
            TimeSpan.FromSeconds(FritzBox.MinimumIntervalSeconds));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), effective);
    }
}
