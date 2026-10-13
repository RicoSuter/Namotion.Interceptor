using HomeBlaze.Abstractions.Common;
using Xunit;

namespace Namotion.Devices.Shelly.Tests;

public class ShellyUptimeStateTests
{
    [Fact]
    public void WhenAShellyDeviceIsCreated_ThenItReportsItsUptimeThroughTheSharedInterface()
    {
        // Act
        var isUptimeState = typeof(IUptimeState).IsAssignableFrom(typeof(ShellyDevice));

        // Assert
        Assert.True(isUptimeState);
    }
}
