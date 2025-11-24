using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Sources.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

public class MqttClientConfigurationTests
{
    [Fact]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var config = new MqttClientConfiguration
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
        var config = new MqttClientConfiguration
        {
            BrokerHost = null!,
            BrokerPort = 1883,
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_EmptyBrokerHost_ThrowsArgumentException()
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "",
            BrokerPort = 1883,
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_InvalidBrokerPort_ThrowsArgumentException(int port)
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = port,
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_NullPathProvider_ThrowsArgumentException()
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = 1883,
            PathProvider = null!
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_NegativeWriteRetryQueueSize_ThrowsArgumentException()
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = 1883,
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null),
            WriteRetryQueueSize = -1
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_MaxReconnectDelayLessThanReconnectDelay_ThrowsArgumentException()
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = 1883,
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null),
            ReconnectDelay = TimeSpan.FromSeconds(10),
            MaxReconnectDelay = TimeSpan.FromSeconds(5)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            PathProvider = new AttributeBasedSourcePathProvider("test", "/", null)
        };

        // Assert
        Assert.Equal(1883, config.BrokerPort);
        Assert.False(config.UseTls);
        Assert.True(config.CleanSession);
        Assert.Equal(TimeSpan.FromSeconds(15), config.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), config.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), config.ReconnectDelay);
        Assert.Equal(TimeSpan.FromMinutes(1), config.MaxReconnectDelay);
        Assert.NotNull(config.ValueConverter);
    }
}
