using HomeBlaze.Abstractions;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class AnalogChannelTests
{
    [Fact]
    public void AnalogChannel_InitializesWithDefaults()
    {
        // Act
        var channel = new AnalogChannel();

        // Assert
        Assert.Equal(0, channel.ChannelNumber);
        Assert.Equal(0.0, channel.Value);
        Assert.Equal(0, channel.RawValue);
    }

    [Fact]
    public void AnalogChannel_InitializesWithStoppedStatus()
    {
        // Act
        var channel = new AnalogChannel();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, channel.Status);
        Assert.Null(channel.StatusMessage);
    }

    [Fact]
    public void AnalogChannel_CanSetStatusProperties()
    {
        // Arrange
        var channel = new AnalogChannel();

        // Act
        channel.Status = ServiceStatus.Running;
        channel.StatusMessage = "Channel is active";

        // Assert
        Assert.Equal(ServiceStatus.Running, channel.Status);
        Assert.Equal("Channel is active", channel.StatusMessage);
    }

    [Fact]
    public void AnalogChannel_CanSetValueProperties()
    {
        // Arrange
        var channel = new AnalogChannel();

        // Act
        channel.ChannelNumber = 3;
        channel.RawValue = 512;
        channel.Value = 0.5;

        // Assert
        Assert.Equal(3, channel.ChannelNumber);
        Assert.Equal(512, channel.RawValue);
        Assert.Equal(0.5, channel.Value);
    }

    [Fact]
    public void AnalogChannel_TitleAndIcon_ReturnExpectedValues()
    {
        // Arrange
        var channel = new AnalogChannel { ChannelNumber = 5 };

        // Act & Assert
        Assert.Equal("Channel 5", channel.Title);
        Assert.Equal("ShowChart", channel.IconName);
    }
}
