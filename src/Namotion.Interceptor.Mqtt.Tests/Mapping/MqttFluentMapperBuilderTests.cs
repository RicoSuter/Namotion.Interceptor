using MQTTnet.Protocol;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Mapping;

public class MqttFluentMapperBuilderTests
{
    private static MqttFluentMapper CreateFluentMapper(MqttFluentMapperBuilder<MqttFluentRoot> fluent)
        => fluent.Build('/');

    [Fact]
    public void WhenTypeMemberMapped_ThenTopicComposesFromSegments()
    {
        // Arrange
        var fluent = new MqttFluentMapperBuilder<MqttFluentRoot>();
        fluent
            .ForType<MqttFluentRoot>()
                .Map(r => r.Pump, b => b.WithSegment("pump"))
            .ForType<MqttFluentPump>()
                .Map(p => p.Speed, b => b.WithSegment("speed").WithQualityOfService(MqttQualityOfServiceLevel.AtLeastOnce).WithRetain(true));
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context) { Pump = new MqttFluentPump() };
        _ = root.TryGetRegisteredSubject()!;
        var speed = root.Pump.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = mapper.TryGetMapping(speed, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("pump/speed", mapping!.Topic);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, mapping.QualityOfService);
        Assert.True(mapping.Retain);
    }

    [Fact]
    public void WhenTypeReusedAcrossLocations_ThenResolvesEverywhere()
    {
        // Arrange
        var fluent = new MqttFluentMapperBuilder<MqttFluentRoot>();
        fluent
            .ForType<MqttFluentRoot>()
                .Map(r => r.Pump, b => b.WithSegment("pump"))
                .Map(r => r.Fan, b => b.WithSegment("fan"))
            .ForType<MqttFluentPump>()
                .Map(p => p.Speed, b => b.WithSegment("speed"));
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context)
        {
            Pump = new MqttFluentPump(),
            Fan = new MqttFluentPump()
        };
        _ = root.TryGetRegisteredSubject()!;

        // Act
        mapper.TryGetMapping(root.Pump.TryGetRegisteredSubject()!.TryGetProperty("Speed")!, root, out var pumpMapping);
        mapper.TryGetMapping(root.Fan.TryGetRegisteredSubject()!.TryGetProperty("Speed")!, root, out var fanMapping);

        // Assert
        Assert.Equal("pump/speed", pumpMapping!.Topic);
        Assert.Equal("fan/speed", fanMapping!.Topic);
    }

    [Fact]
    public async Task WhenReverseLookup_ThenResolvesViaPathProvider()
    {
        // Arrange
        var fluent = new MqttFluentMapperBuilder<MqttFluentRoot>();
        fluent
            .ForType<MqttFluentRoot>().Map(r => r.Pump, b => b.WithSegment("pump"))
            .ForType<MqttFluentPump>().Map(p => p.Speed, b => b.WithSegment("speed"));
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context) { Pump = new MqttFluentPump() };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("pump/speed"), registeredRoot, CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Speed", found.Name);
    }

    [Fact]
    public void WhenPropertyNotMapped_ThenReturnsFalse()
    {
        // Arrange
        var fluent = new MqttFluentMapperBuilder<MqttFluentRoot>();
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context) { Pump = new MqttFluentPump() };
        _ = root.TryGetRegisteredSubject()!;
        var speed = root.Pump.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = mapper.TryGetMapping(speed, root, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public async Task WhenTypeUsedInCollection_ThenElementResolvesBothDirections()
    {
        // Arrange
        var fluent = new MqttFluentMapperBuilder<MqttFluentRoot>();
        fluent
            .ForType<MqttFluentRoot>().Map(r => r.Motors, b => b.WithSegment("motors"))
            .ForType<MqttFluentPump>().Map(p => p.Speed, b => b.WithSegment("speed"));
        var mapper = CreateFluentMapper(fluent);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new MqttFluentRoot(context) { Motors = [new MqttFluentPump(), new MqttFluentPump()] };
        var registeredRoot = root.TryGetRegisteredSubject()!;
        var speed = root.Motors[1].TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var forwardFound = mapper.TryGetMapping(speed, root, out var mapping);
        var reverse = await mapper.TryGetPropertyAsync(
            new MqttLookupKey("motors[1]/speed"), registeredRoot, CancellationToken.None);

        // Assert
        Assert.True(forwardFound);
        Assert.Equal("motors[1]/speed", mapping!.Topic);
        Assert.NotNull(reverse);
        Assert.Equal("Speed", reverse.Name);
    }
}

[InterceptorSubject]
public partial class MqttFluentRoot
{
    public partial MqttFluentPump Pump { get; set; }
    public partial MqttFluentPump Fan { get; set; }
    public partial List<MqttFluentPump> Motors { get; set; }

    public MqttFluentRoot()
    {
        Pump = null!;
        Fan = null!;
        Motors = [];
    }
}

[InterceptorSubject]
public partial class MqttFluentPump
{
    public partial double Speed { get; set; }

    public MqttFluentPump()
    {
        Speed = 0;
    }
}
