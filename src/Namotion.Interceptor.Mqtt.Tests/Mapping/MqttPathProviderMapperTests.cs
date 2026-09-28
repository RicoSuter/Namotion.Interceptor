using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttPathProviderMapperTests
{
    [Fact]
    public void WhenPropertyHasPathAttribute_ThenReturnsMappingWithTopic()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("mqtt", '/');
        var mapper = new MqttPathProviderMapper(pathProvider);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttPathTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("Temperature", mapping!.Topic);
    }

    [Fact]
    public void WhenPropertyHasNoPathAttribute_ThenReturnsFalse()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("mqtt", '/');
        var mapper = new MqttPathProviderMapper(pathProvider);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttPathTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Unmapped")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void WhenMappingReturned_ThenQosAndRetainAreNull()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("mqtt", '/');
        var mapper = new MqttPathProviderMapper(pathProvider);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttPathTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.TryGetProperty("Temperature")!;

        // Act
        mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.Null(mapping!.QualityOfService);
        Assert.Null(mapping.Retain);
    }

    [Fact]
    public async Task WhenReverseLookupWithValidTopic_ThenReturnsProperty()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("mqtt", '/');
        var mapper = new MqttPathProviderMapper(pathProvider);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttPathTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("Temperature"), registeredSubject, CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Temperature", found.Name);
    }

    [Fact]
    public async Task WhenReverseLookupWithUnknownTopic_ThenReturnsNull()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("mqtt", '/');
        var mapper = new MqttPathProviderMapper(pathProvider);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MqttPathTestSensor(context);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("unknown/topic"), registeredSubject, CancellationToken.None);

        // Assert
        Assert.Null(found);
    }
}

[InterceptorSubject]
public partial class MqttPathTestSensor
{
    [Path("mqtt", "Temperature")]
    public partial double Temperature { get; set; }

    public partial double Unmapped { get; set; }

    public MqttPathTestSensor()
    {
        Temperature = 0;
        Unmapped = 0;
    }
}
