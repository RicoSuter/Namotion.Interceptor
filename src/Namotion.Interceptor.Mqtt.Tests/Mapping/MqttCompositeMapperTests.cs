using MQTTnet.Protocol;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttCompositeMapperTests
{
    private static MqttCompositeMapper CreateDefaultMapper() => new(
        new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
        new MqttAttributeMapper());

    [Fact]
    public void WhenNoMappersMatch_ThenReturnsFalse()
    {
        // Arrange
        var mapper = CreateDefaultMapper();

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
    public void WhenComposite_ThenTopicComesFromPathProviderAndMetadataFromAttribute()
    {
        // Arrange
        var mapper = CreateDefaultMapper();

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert - the topic is the path-provider-resolved segment; QoS/Retain come from the attribute
        Assert.True(found);
        Assert.Equal("temp", mapping!.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, mapping.QualityOfService);
        Assert.True(mapping.Retain);
    }

    [Fact]
    public void WhenFluentTopicAndAttributeMetadata_ThenAttributeMetadataLayersOntoFluentTopic()
    {
        // Arrange
        var fluent = new MqttFluentMapper<MqttCompositeTestSensor>()
            .Map(s => s.Temperature, b => b
                .WithTopic("fluent/temp"));

        var mapper = new MqttCompositeMapper(
            fluent,
            new MqttAttributeMapper());

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        mapper.TryGetMapping(property, subject, out var mapping);

        // Assert - the attribute contributes no topic, so the fluent topic falls through;
        // the attribute's QoS/Retain layer on top.
        Assert.Equal("fluent/temp", mapping!.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, mapping.QualityOfService);
        Assert.True(mapping.Retain);
    }

    [Fact]
    public async Task WhenReverseLookup_ThenResolvedViaPathProvider()
    {
        // Arrange
        var mapper = CreateDefaultMapper();

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttCompositeTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("temp"), registeredSubject, CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Temperature", found.Name);
    }

    [Fact]
    public async Task WhenReverseLookupNoMatch_ThenReturnsNull()
    {
        // Arrange
        var mapper = CreateDefaultMapper();

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
    [MqttTopic("temp", QualityOfService = MqttQualityOfServiceLevel.ExactlyOnce, Retain = MqttRetainMode.True)]
    public partial double Temperature { get; set; }

    public partial double Unmapped { get; set; }

    public MqttCompositeTestSensor()
    {
        Temperature = 0;
        Unmapped = 0;
    }
}
