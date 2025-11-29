using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Sources.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

public class MqttServerConfigurationTests
{
    [Fact]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var config = new MqttServerConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = 1883,
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null)
        };

        // Act & Assert
        config.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_NullBrokerHost_ThrowsArgumentException()
    {
        // Arrange
        var config = new MqttServerConfiguration
        {
            BrokerHost = null!,
            BrokerPort = 1883,
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_NullPathProvider_ThrowsArgumentException()
    {
        // Arrange
        var config = new MqttServerConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = 1883,
            PathProvider = null!
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new MqttServerConfiguration
        {
            BrokerHost = "localhost",
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null)
        };

        // Assert
        Assert.Equal(1883, config.BrokerPort);
        Assert.Equal(10000, config.MaxPendingMessagesPerClient);
        Assert.NotNull(config.ValueConverter);
    }
}
