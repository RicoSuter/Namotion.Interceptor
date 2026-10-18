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

    [Fact]
    public void WhenDeviceIsDisconnected_ThenNeitherOperationIsEnabled()
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
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
