using MQTTnet.Protocol;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttAttributePropertyMapperTests
{
    [Fact]
    public void WhenPropertyHasMqttTopicAttribute_ThenReturnsMappingWithTopic()
    {
        // Arrange
        var mapper = new MqttAttributePropertyMapper();
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        var result = mapper.TryGetMapping(property, out var mapping);

        // Assert
        Assert.True(result);
        Assert.Equal("sensors/temperature", mapping!.Topic);
    }

    [Fact]
    public void WhenPropertyHasNoMqttTopicAttribute_ThenReturnsNull()
    {
        // Arrange
        var mapper = new MqttAttributePropertyMapper();
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Unmapped")!;

        // Act
        var result = mapper.TryGetMapping(property, out var mapping);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenPropertyHasQosAndRetain_ThenReturnsMappingWithThoseValues()
    {
        // Arrange
        var mapper = new MqttAttributePropertyMapper();
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Humidity")!;

        // Act
        var result = mapper.TryGetMapping(property, out var mapping);

        // Assert
        Assert.True(result);
        Assert.Equal("sensors/humidity", mapping!.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, mapping.QualityOfService);
        Assert.True(mapping.Retain);
    }

    [Fact]
    public void WhenPropertyHasDefaultQos_ThenQosIsNull()
    {
        // Arrange
        var mapper = new MqttAttributePropertyMapper();
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        mapper.TryGetMapping(property, out var mapping);

        // Assert
        Assert.Null(mapping!.QualityOfService);
        Assert.Null(mapping.Retain);
    }

    [Fact]
    public void WhenConnectorNameDoesNotMatch_ThenReturnsNull()
    {
        // Arrange
        var mapper = new MqttAttributePropertyMapper("other-connector");
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        var result = mapper.TryGetMapping(property, out _);

        // Assert
        Assert.False(result);
    }
}

[InterceptorSubject]
public partial class MqttAttributeTestSensor
{
    [MqttTopic("sensors/temperature")]
    public partial double Temperature { get; set; }

    [MqttTopic("sensors/humidity", QualityOfService = MqttQualityOfServiceLevel.ExactlyOnce, Retain = true, RetainSet = true)]
    public partial double Humidity { get; set; }

    public partial double Unmapped { get; set; }

    public MqttAttributeTestSensor()
    {
        Temperature = 0;
        Humidity = 0;
        Unmapped = 0;
    }
}
