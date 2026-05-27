using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

public class MqttServerConfigurationTests
{
    private static IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> CreateTestMapper() =>
        new MqttPathProviderPropertyMapper(new AttributeBasedPathProvider("test", '/'));

    [Fact]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var config = new MqttServerConfiguration
        {
            BrokerPort = 1883,
            Mapper = CreateTestMapper()
        };

        // Act & Assert
        config.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_WithBrokerHost_DoesNotThrow()
    {
        // Arrange
        var config = new MqttServerConfiguration
        {
            BrokerHost = "127.0.0.1",
            BrokerPort = 1883,
            Mapper = CreateTestMapper()
        };

        // Act & Assert
        config.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_NullMapper_ThrowsArgumentException()
    {
        // Arrange
        var config = new MqttServerConfiguration
        {
            BrokerPort = 1883,
            Mapper = null!
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
            Mapper = CreateTestMapper()
        };

        // Assert
        Assert.Null(config.BrokerHost); // Default is null (bind to all interfaces)
        Assert.Equal(1883, config.BrokerPort);
        Assert.Equal(25000, config.MaxPendingMessagesPerClient);
        Assert.NotNull(config.ValueConverter);
    }

    [Fact]
    public void DefaultTimestampSerializerAndDeserializer_Roundtrip()
    {
        // Arrange
        var config = new MqttServerConfiguration
        {
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
