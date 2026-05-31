using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class OpcUaFluentMappingTests
{
    [Fact]
    public void WhenLeafMapped_ThenBrowseNameIsSegmentAndMetadata()
    {
        // Arrange
        var fluent = new OpcUaFluentMapping<OpcUaFluentRoot>();
        fluent.ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed").SamplingInterval(500));

        var mappers = fluent.CreateMappers('.');
        var metadataMapper = (OpcUaFluentMetadataMapper)mappers[1];

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var speed = root.Motor.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = metadataMapper.TryGetMapping(speed, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("Speed", mapping!.BrowseName);
        Assert.Equal(500, mapping.SamplingInterval);
    }

    [Fact]
    public void WhenTypeReusedAcrossLocations_ThenBrowseNameResolvesEverywhere()
    {
        // Arrange
        var fluent = new OpcUaFluentMapping<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>()
                .Map(r => r.Motor, b => b.BrowseName("Motor"))
            .ForType<OpcUaFluentMotor>()
                .Map(m => m.Speed, b => b.BrowseName("Speed"));
        var mappers = fluent.CreateMappers('.');
        var pathMapper = (OpcUaPathProviderMapper)mappers[0];

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var motorProperty = root.TryGetRegisteredSubject()!.TryGetProperty("Motor")!;
        var speedProperty = root.Motor.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        pathMapper.TryGetMapping(motorProperty, root, out var motorMapping);
        pathMapper.TryGetMapping(speedProperty, root, out var speedMapping);

        // Assert - the path-provider mapper supplies the browse name from the fluent segment.
        Assert.Equal("Motor", motorMapping!.BrowseName);
        Assert.Equal("Speed", speedMapping!.BrowseName);
    }

    [Fact]
    public void WhenConfigureUsedForReferencedType_ThenTypeSelfMergesIntoSubjectMember()
    {
        // Arrange
        var fluent = new OpcUaFluentMapping<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>()
                .Map(r => r.Motor, b => b.BrowseName("Motor"))
            .ForType<OpcUaFluentMotor>()
                .Configure(b => b.TypeDefinition("MotorType"));
        var metadataMapper = (OpcUaFluentMetadataMapper)fluent.CreateMappers('.')[1];

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var motorProperty = root.TryGetRegisteredSubject()!.TryGetProperty("Motor")!;

        // Act
        var found = metadataMapper.TryGetMapping(motorProperty, root, out var mapping);

        // Assert - the Motor member's metadata (BrowseName) plus its type-self TypeDefinition.
        Assert.True(found);
        Assert.Equal("Motor", mapping!.BrowseName);
        Assert.Equal("MotorType", mapping.TypeDefinition);
    }
}

[InterceptorSubject]
public partial class OpcUaFluentRoot
{
    public partial OpcUaFluentMotor Motor { get; set; }

    public OpcUaFluentRoot()
    {
        Motor = null!;
    }
}

[InterceptorSubject]
public partial class OpcUaFluentMotor
{
    public partial double Speed { get; set; }

    public OpcUaFluentMotor()
    {
        Speed = 0;
    }
}
