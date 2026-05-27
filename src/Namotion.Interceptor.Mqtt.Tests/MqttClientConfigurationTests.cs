using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

public class MqttClientConfigurationTests
{
    private static IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> CreateTestMapper() =>
        new MqttPathProviderPropertyMapper(new AttributeBasedPathProvider("test", '/'));

    [Fact]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = 1883,
            Mapper = CreateTestMapper()
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
            Mapper = CreateTestMapper()
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
            Mapper = CreateTestMapper()
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
            Mapper = CreateTestMapper()
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_NullMapper_ThrowsArgumentException()
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            BrokerPort = 1883,
            Mapper = null!
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
            Mapper = CreateTestMapper(),
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
            Mapper = CreateTestMapper(),
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
            Mapper = CreateTestMapper()
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

    [Fact]
    public void DefaultTimestampSerializerAndDeserializer_Roundtrip()
    {
        // Arrange
        var config = new MqttClientConfiguration
        {
            BrokerHost = "localhost",
            Mapper = CreateTestMapper()
        };
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var serialized = config.SourceTimestampSerializer(timestamp);
        var deserialized = config.SourceTimestampDeserializer(serialized);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(timestamp.ToUnixTimeMilliseconds(), deserialized!.Value.ToUnixTimeMilliseconds());
    }
}
