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
