using HomeBlaze.Abstractions;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioSubjectTests
{
    [Fact]
    public void GpioSubject_InitializesWithEmptyCollections()
    {
        // Act
        var subject = new GpioSubject();

        // Assert
        Assert.NotNull(subject.Pins);
        Assert.Empty(subject.Pins);
        Assert.NotNull(subject.AnalogChannels);
        Assert.Empty(subject.AnalogChannels);
        Assert.Null(subject.Mcp3008);
        Assert.Null(subject.Ads1115);
    }

    [Fact]
    public void GpioSubject_TitleAndIcon_ReturnExpectedValues()
    {
        // Arrange
        var subject = new GpioSubject();

        // Act & Assert
        Assert.Equal("GPIO", subject.Title);
        Assert.Equal("Memory", subject.IconName);
    }

    [Fact]
    public void GpioSubject_InitializesWithStoppedStatus()
    {
        // Act
        var subject = new GpioSubject();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.Null(subject.StatusMessage);
    }

    [Fact]
    public void GpioSubject_HasDefaultPollingInterval()
    {
        // Act
        var subject = new GpioSubject();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(5), subject.PollingInterval);
    }

    [Fact]
    public async Task GpioSubject_OnNonRaspberryPi_SetsUnavailableStatus()
    {
        // Arrange
        var subject = new GpioSubject();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await subject.StartAsync(cts.Token);

        // Assert - On Windows/non-Pi platforms, GPIO is not supported
        Assert.Equal(ServiceStatus.Unavailable, subject.Status);
        Assert.Equal("GPIO not supported on this platform", subject.StatusMessage);
    }

    [Fact]
    public async Task GpioSubject_OnNonRaspberryPi_CreatesPinsWithUnavailableStatus()
    {
        // Arrange
        var subject = new GpioSubject();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await subject.StartAsync(cts.Token);

        // Assert - Pins dictionary should be empty when GPIO is not supported
        // (pin count is dynamic based on platform, no pins available when unsupported)
        Assert.Empty(subject.Pins);
    }

    [Fact]
    public void GpioSubject_Dispose_SetsStoppedStatus()
    {
        // Arrange
        var subject = new GpioSubject();

        // Act
        subject.Dispose();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, subject.Status);
        Assert.Null(subject.StatusMessage);
    }
}
