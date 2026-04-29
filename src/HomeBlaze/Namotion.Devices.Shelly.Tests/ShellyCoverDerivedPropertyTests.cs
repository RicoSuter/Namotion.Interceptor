using HomeBlaze.Abstractions.Devices.Covers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.Shelly.Tests;

public class ShellyCoverDerivedPropertyTests
{
    private static (ShellyDevice device, ShellyCover cover) CreateCover()
    {
        var httpClientFactory = new TestHttpClientFactory();
        var device = new ShellyDevice(httpClientFactory, NullLogger<ShellyDevice>.Instance);
        var cover = new ShellyCover(device, 0);
        return (device, cover);
    }

    [Theory]
    [InlineData(0, 1.0)]     // API 0 = closed → Position 1.0 (closed)
    [InlineData(100, 0.0)]   // API 100 = open → Position 0.0 (open)
    [InlineData(50, 0.5)]    // API 50 = half → Position 0.5
    [InlineData(75, 0.25)]   // API 75 → Position 0.25
    public void WhenCurrentPositionSet_ThenPositionMapsCorrectly(int apiPosition, decimal expectedPosition)
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.CurrentPosition = apiPosition;

        // Assert
        Assert.Equal(expectedPosition, cover.Position);
    }

    [Fact]
    public void WhenCurrentPositionNull_ThenPositionIsNull()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Assert
        Assert.Null(cover.Position);
    }

    [Fact]
    public void WhenApiStateIsOpening_ThenShutterStateIsOpening()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.ApiState = "opening";

        // Assert
        Assert.Equal(RollerShutterState.Opening, cover.ShutterState);
    }

    [Fact]
    public void WhenApiStateIsClosing_ThenShutterStateIsClosing()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.ApiState = "closing";

        // Assert
        Assert.Equal(RollerShutterState.Closing, cover.ShutterState);
    }

    [Fact]
    public void WhenStoppedAndFullyOpen_ThenShutterStateIsOpen()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.ApiState = "stopped";
        cover.CurrentPosition = 100; // API 100 = open → Position 0

        // Assert
        Assert.Equal(RollerShutterState.Open, cover.ShutterState);
    }

    [Fact]
    public void WhenStoppedAndFullyClosed_ThenShutterStateIsClosed()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.ApiState = "stopped";
        cover.CurrentPosition = 0; // API 0 = closed → Position 1

        // Assert
        Assert.Equal(RollerShutterState.Closed, cover.ShutterState);
    }

    [Fact]
    public void WhenStoppedAndPartiallyOpen_ThenShutterStateIsPartiallyOpen()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.ApiState = "stopped";
        cover.CurrentPosition = 50;

        // Assert
        Assert.Equal(RollerShutterState.PartiallyOpen, cover.ShutterState);
    }

    [Fact]
    public void WhenCalibrating_ThenShutterStateIsCalibrating()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.ApiState = "calibrating";

        // Assert
        Assert.Equal(RollerShutterState.Calibrating, cover.ShutterState);
    }

    [Fact]
    public void WhenPowerAboveThreshold_ThenIsMovingTrue()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.MeasuredPower = 50m;

        // Assert
        Assert.True(cover.IsMoving);
    }

    [Fact]
    public void WhenPowerBelowThreshold_ThenIsMovingFalse()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Act
        cover.MeasuredPower = 0.5m;

        // Assert
        Assert.False(cover.IsMoving);
    }

    [Fact]
    public void WhenPowerNull_ThenIsMovingNull()
    {
        // Arrange
        var (_, cover) = CreateCover();

        // Assert
        Assert.Null(cover.IsMoving);
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
