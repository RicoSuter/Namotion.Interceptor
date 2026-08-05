using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueProcessorConfigurationTests
{
    [Fact]
    public void WhenUnbounded_ThenValidatePasses()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration();

        // Act & Assert
        configuration.Validate();
    }

    [Fact]
    public void WhenBoundedWithPositiveMaxQueueSize_ThenValidatePasses()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration
        {
            OverflowBehavior = OverflowBehavior.DropOldest,
            MaxQueueSize = 100,
        };

        // Act & Assert
        configuration.Validate();
    }

    [Fact]
    public void WhenBoundedWithoutMaxQueueSize_ThenValidateThrows()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration
        {
            OverflowBehavior = OverflowBehavior.DropNewest,
            MaxQueueSize = null,
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void WhenBoundedWithNonPositiveMaxQueueSize_ThenValidateThrows()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration
        {
            OverflowBehavior = OverflowBehavior.DropOldest,
            MaxQueueSize = 0,
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void WhenDefault_ThenUnboundedWithEightMillisecondBufferTime()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration();

        // Act & Assert
        Assert.Equal(OverflowBehavior.Unbounded, configuration.OverflowBehavior);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
        Assert.Null(configuration.MaxQueueSize);
    }
}
