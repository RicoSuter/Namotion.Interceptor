using HomeBlaze.Abstractions.Devices.Covers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.Shelly.Tests;

public class ShellyCoverEnabledOperationTests
{
    private static (ShellyDevice device, ShellyCover cover) CreateConnectedCover()
    {
        var device = new ShellyDevice(new TestHttpClientFactory(), NullLogger<ShellyDevice>.Instance);
        device.IsConnected = true;
        return (device, new ShellyCover(device, 0));
    }

    [Theory]
    [InlineData("stopped", 100, false, true)]  // fully open: only Close
    [InlineData("stopped", 0, true, false)]    // fully closed: only Open
    [InlineData("stopped", 50, true, true)]    // partly open: both
    [InlineData("opening", 50, false, true)]   // opening: Open would do nothing
    [InlineData("closing", 50, true, false)]   // closing: Close would do nothing
    public void WhenCoverIsInAState_ThenOnlyTheOperationsThatMoveItAreEnabled(
        string apiState, int apiPosition, bool expectedOpen, bool expectedClose)
    {
        // Arrange
        var (_, cover) = CreateConnectedCover();

        // Act
        cover.ApiState = apiState;
        cover.CurrentPosition = apiPosition;

        // Assert
        Assert.Equal(expectedOpen, cover.Open_IsEnabled);
        Assert.Equal(expectedClose, cover.Close_IsEnabled);
    }

    [Theory]
    [InlineData("opening", 50, true)]
    [InlineData("closing", 50, true)]
    [InlineData("stopped", 100, false)]
    [InlineData("stopped", 0, false)]
    [InlineData("stopped", 50, false)]
    [InlineData("calibrating", 50, true)]
    [InlineData(null, null, true)]             // state not known yet: it may move
    public void WhenCoverIsInAState_ThenStopIsDisabledOnlyWhileItIsKnownToStandStill(
        string? apiState, int? apiPosition, bool expectedStop)
    {
        // Arrange
        var (_, cover) = CreateConnectedCover();

        // Act
        cover.ApiState = apiState;
        cover.CurrentPosition = apiPosition;

        // Assert
        Assert.Equal(expectedStop, cover.Stop_IsEnabled);
    }

    [Fact]
    public void WhenCoverDrawsPowerWhileItsStateSaysStopped_ThenStopIsEnabled()
    {
        // Arrange
        var (_, cover) = CreateConnectedCover();
        cover.ApiState = "stopped";
        cover.CurrentPosition = 50;

        // Act
        cover.MeasuredPower = 80;

        // Assert
        Assert.True(cover.Stop_IsEnabled);
    }

    [Theory]
    [InlineData("opening", true, false)]
    [InlineData("closing", false, true)]
    [InlineData("stopped", false, false)]
    public void WhenCoverIsInAState_ThenTheOperationThatMovesItIsActive(
        string apiState, bool expectedOpen, bool expectedClose)
    {
        // Arrange
        var (_, cover) = CreateConnectedCover();

        // Act
        cover.ApiState = apiState;
        cover.CurrentPosition = 50;

        // Assert
        Assert.Equal(expectedOpen, cover.Open_IsActive);
        Assert.Equal(expectedClose, cover.Close_IsActive);
    }

    [Fact]
    public void WhenDeviceIsDisconnected_ThenNoOperationIsEnabled()
    {
        // Arrange
        var (device, cover) = CreateConnectedCover();
        cover.ApiState = "stopped";
        cover.CurrentPosition = 50;

        // Act
        device.IsConnected = false;

        // Assert
        Assert.False(cover.Open_IsEnabled);
        Assert.False(cover.Close_IsEnabled);
        Assert.False(cover.Stop_IsEnabled);
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
