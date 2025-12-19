using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioSubjectTests
{
    [Fact]
    public void GpioSubject_InitializesWithEmptyCollections()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var subject = new GpioSubject(context);

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
        var context = InterceptorSubjectContext.Create();
        var subject = new GpioSubject(context);

        // Act & Assert
        Assert.Equal("GPIO", subject.Title);
        Assert.Equal("Memory", subject.Icon);
    }
}
