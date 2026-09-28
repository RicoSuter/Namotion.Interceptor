using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttNestedTopicCompositionTests
{
    [Fact]
    public void WhenRelativeTopicOnNestedSubject_ThenForwardComposesHierarchically()
    {
        // Arrange - a relative [MqttTopic] segment on a nested subject should compose with the parent
        // reference property's segment into a full hierarchical topic.
        var pathProvider = new AttributeBasedPathProvider("mqtt", '/');
        var mapper = new MqttPathProviderMapper(pathProvider);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttNestedRoot(context) { Device = new MqttNestedChild() };
        var temperature = root.Device!.TryGetRegisteredSubject()!.TryGetProperty("Temperature")!;

        // Act
        var found = mapper.TryGetMapping(temperature, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("device/temperature", mapping!.Topic);
    }

    [Fact]
    public async Task WhenRelativeTopicOnNestedSubject_ThenReverseResolvesComposedTopic()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("mqtt", '/');
        var mapper = new MqttPathProviderMapper(pathProvider);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttNestedRoot(context) { Device = new MqttNestedChild() };
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("device/temperature"), rootRegistered, CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Temperature", found.Name);
    }
}

[InterceptorSubject]
public partial class MqttNestedRoot
{
    [MqttTopic("device")]
    public partial MqttNestedChild? Device { get; set; }
}

[InterceptorSubject]
public partial class MqttNestedChild
{
    [MqttTopic("temperature")]
    public partial double Temperature { get; set; }

    public MqttNestedChild()
    {
        Temperature = 0;
    }
}
