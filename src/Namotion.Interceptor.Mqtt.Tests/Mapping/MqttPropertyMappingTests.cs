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

    [Fact]
    public void WhenPrimaryIsAllNull_ThenAllFieldsComeFromFallback()
    {
        // Arrange
        var fallback = new MqttPropertyMapping(
            Topic: "fallback/topic", QualityOfService: MqttQualityOfServiceLevel.ExactlyOnce, Retain: true);
        var primary = new MqttPropertyMapping();

        // Act
        var merged = MqttPropertyMapping.Merge(primary, fallback);

        // Assert
        Assert.Equal("fallback/topic", merged.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, merged.QualityOfService);
        Assert.True(merged.Retain);
    }

    [Fact]
    public void WhenFallbackIsAllNull_ThenAllFieldsComeFromPrimary()
    {
        // Arrange
        var fallback = new MqttPropertyMapping();
        var primary = new MqttPropertyMapping(
            Topic: "primary/topic", QualityOfService: MqttQualityOfServiceLevel.AtLeastOnce, Retain: false);

        // Act
        var merged = MqttPropertyMapping.Merge(primary, fallback);

        // Assert
        Assert.Equal("primary/topic", merged.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, merged.QualityOfService);
        Assert.False(merged.Retain);
    }

    [Fact]
    public void WhenRetainIsFalseInPrimary_ThenItIsNotTreatedAsUnset()
    {
        // Arrange - false is a meaningful value, not "unset" (only null falls through)
        var fallback = new MqttPropertyMapping(Retain: true);
        var primary = new MqttPropertyMapping(Retain: false);

        // Act
        var merged = MqttPropertyMapping.Merge(primary, fallback);

        // Assert
        Assert.False(merged.Retain);
    }
}
