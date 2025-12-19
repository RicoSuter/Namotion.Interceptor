using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

public class MqttClientConfigurationTests
{
    private static PathProviderBase CreateTestPathProvider() =>
        new AttributeBasedPathProvider("test", '/');

    [Fact]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = 1883,
            PathProvider = CreateTestPathProvider()
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
            PathProvider = CreateTestPathProvider()
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
            PathProvider = CreateTestPathProvider()
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
            PathProvider = CreateTestPathProvider()
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
            PathProvider = CreateTestPathProvider(),
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
            PathProvider = CreateTestPathProvider(),
            ReconnectDelay = TimeSpan.FromSeconds(10),
            MaximumReconnectDelay = TimeSpan.FromSeconds(5)
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
            PathProvider = CreateTestPathProvider()
        };

        // Assert
        Assert.Equal(1883, config.BrokerPort);
        Assert.False(config.UseTls);
        Assert.True(config.CleanSession);
        Assert.Equal(TimeSpan.FromSeconds(15), config.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), config.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), config.ReconnectDelay);
        Assert.Equal(TimeSpan.FromMinutes(1), config.MaximumReconnectDelay);
        Assert.NotNull(config.ValueConverter);
    }
}
