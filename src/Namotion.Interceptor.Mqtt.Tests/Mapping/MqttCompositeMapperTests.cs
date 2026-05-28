using MQTTnet.Protocol;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttCompositeMapperTests
{
    [Fact]
    public void WhenNoMappersMatch_ThenReturnsFalse()
    {
        // Arrange
        var mapper = new MqttCompositeMapper(
            new MqttAttributeMapper("nonexistent"));

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Unmapped")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void WhenSingleMapperMatches_ThenReturnsItsMapping()
    {
        // Arrange
        var mapper = new MqttCompositeMapper(
            new MqttAttributeMapper());

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("sensors/temp", mapping!.Topic);
    }

    [Fact]
    public void WhenMappersMatch_ThenLaterTopicWinsAndUnsetFieldsFallThrough()
    {
        // Arrange
        var fluent = new MqttFluentMapper<MqttCompositeTestSensor>()
            .Map(s => s.Temperature, b => b
                .WithTopic("fluent/temp")
                .WithQualityOfService(MqttQualityOfServiceLevel.ExactlyOnce));

        var mapper = new MqttCompositeMapper(
            fluent,
            new MqttAttributeMapper());

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.Equal("sensors/temp", mapping!.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, mapping.QualityOfService);
    }

    [Fact]
    public async Task WhenReverseLookup_ThenLastMapperWins()
    {
        // Arrange
        var mapper = new MqttCompositeMapper(
            new MqttAttributeMapper());

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("sensors/temp"), registeredSubject, CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Temperature", found.Name);
    }

    [Fact]
    public async Task WhenReverseLookupNoMatch_ThenReturnsNull()
    {
        // Arrange
        var mapper = new MqttCompositeMapper(
            new MqttAttributeMapper());

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("nonexistent/topic"), registeredSubject, CancellationToken.None);

        // Assert
        Assert.Null(found);
    }
}

[InterceptorSubject]
public partial class MqttCompositeTestSensor
{
    [MqttTopic("sensors/temp")]
    public partial double Temperature { get; set; }

    public partial double Unmapped { get; set; }

    public MqttCompositeTestSensor()
    {
        Temperature = 0;
        Unmapped = 0;
    }
}
