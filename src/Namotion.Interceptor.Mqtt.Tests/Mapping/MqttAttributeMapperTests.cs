using MQTTnet.Protocol;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttAttributeMapperTests
{
    [Fact]
    public void WhenPropertyHasMqttTopicAttribute_ThenReturnsMappingWithTopic()
    {
        // Arrange
        var mapper = new MqttAttributeMapper();
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        var result = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.True(result);
        Assert.Equal("sensors/temperature", mapping!.Topic);
    }

    [Fact]
    public void WhenPropertyHasNoMqttTopicAttribute_ThenReturnsNull()
    {
        // Arrange
        var mapper = new MqttAttributeMapper();
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Unmapped")!;

        // Act
        var result = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenPropertyHasQosAndRetain_ThenReturnsMappingWithThoseValues()
    {
        // Arrange
        var mapper = new MqttAttributeMapper();
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Humidity")!;

        // Act
        var result = mapper.TryGetMapping(property, subject, out var mapping);

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
        var mapper = new MqttAttributeMapper();
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.Null(mapping!.QualityOfService);
        Assert.Null(mapping.Retain);
    }

    [Fact]
    public void WhenConnectorNameDoesNotMatch_ThenReturnsNull()
    {
        // Arrange
        var mapper = new MqttAttributeMapper("other-connector");
        var subject = new MqttAttributeTestSensor(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        var result = mapper.TryGetMapping(property, subject, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WhenReverseLookupWithMatchingTopic_ThenReturnsProperty()
    {
        // Arrange
        var mapper = new MqttAttributeMapper();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttAttributeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("sensors/temperature"), registeredSubject, CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Temperature", found.Name);
    }

    [Fact]
    public async Task WhenReverseLookupWithUnknownTopic_ThenReturnsNull()
    {
        // Arrange
        var mapper = new MqttAttributeMapper();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttAttributeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("nonexistent/topic"), registeredSubject, CancellationToken.None);

        // Assert
        Assert.Null(found);
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
