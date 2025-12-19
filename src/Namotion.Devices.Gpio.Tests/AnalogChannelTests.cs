using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class AnalogChannelTests
{
    [Fact]
    public void AnalogChannel_InitializesWithDefaults()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var channel = new AnalogChannel(context);

        // Assert
        Assert.Equal(0, channel.ChannelNumber);
        Assert.Equal(0.0, channel.Value);
        Assert.Equal(0, channel.RawValue);
    }
}
