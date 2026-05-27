using MQTTnet.Protocol;
using Namotion.Interceptor.Mqtt.Mapping;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttPropertyMappingTests
{
    [Fact]
    public void WhenMergingPartialMappings_ThenPrimaryPrecedesFallbackPerField()
    {
        // Arrange
        var fallback = new MqttPropertyMapping(
            Topic: "fallback/topic", QualityOfService: MqttQualityOfServiceLevel.AtMostOnce, Retain: false);
        var primary = new MqttPropertyMapping(Topic: "primary/topic", QualityOfService: null, Retain: true);

        // Act
        var merged = MqttPropertyMapping.Merge(primary, fallback);

        // Assert
        Assert.Equal("primary/topic", merged.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.AtMostOnce, merged.QualityOfService);
        Assert.True(merged.Retain);
    }
}
