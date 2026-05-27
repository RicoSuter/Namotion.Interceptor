using MQTTnet.Protocol;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttFluentMapperTests
{
    [Fact]
    public void WhenPropertyIsMapped_ThenReturnsMappingWithConfiguredValues()
    {
        // Arrange
        var mapper = new MqttFluentMapper<MqttFluentTestSensor>()
            .Map(s => s.Temperature, b => b
                .WithTopic("sensors/temperature")
                .WithQualityOfService(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetain(true));

        var subject = new MqttFluentTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        var result = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.True(result);
        Assert.Equal("sensors/temperature", mapping!.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, mapping.QualityOfService);
        Assert.True(mapping.Retain);
    }

    [Fact]
    public void WhenPropertyIsNotMapped_ThenReturnsFalse()
    {
        // Arrange
        var mapper = new MqttFluentMapper<MqttFluentTestSensor>()
            .Map(s => s.Temperature, b => b.WithTopic("sensors/temperature"));

        var subject = new MqttFluentTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Unmapped")!;

        // Act
        var result = mapper.TryGetMapping(property, subject, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenTopicOnly_ThenQosAndRetainAreNull()
    {
        // Arrange
        var mapper = new MqttFluentMapper<MqttFluentTestSensor>()
            .Map(s => s.Temperature, b => b.WithTopic("sensors/temperature"));

        var subject = new MqttFluentTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.Equal("sensors/temperature", mapping!.Topic);
        Assert.Null(mapping.QualityOfService);
        Assert.Null(mapping.Retain);
    }
}

[InterceptorSubject]
public partial class MqttFluentTestSensor
{
    public partial double Temperature { get; set; }

    public partial double Unmapped { get; set; }

    public MqttFluentTestSensor()
    {
        Temperature = 0;
        Unmapped = 0;
    }
}
