using Moq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
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

    [Fact]
    public async Task WhenFluentBrowseNameConfigured_ThenReverseLookupResolvesSpeedProperty()
    {
        // Arrange
        var fluent = new OpcUaFluentMapping<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>().Map(r => r.Motor, b => b.BrowseName("Motor"))
            .ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed"));
        var mappers = fluent.CreateMappers('.');
        var pathProviderMapper = (OpcUaPathProviderMapper)mappers[0];

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var motorRegisteredSubject = root.Motor.TryGetRegisteredSubject()!;

        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(new NamespaceTable());

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("Speed", 0),
            BrowseName = new QualifiedName("Speed", 0)
        };

        // Act
        var result = await pathProviderMapper.TryGetPropertyAsync(
            new OpcUaLookupKey(nodeReference, mockSession.Object, root),
            motorRegisteredSubject,
            CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Speed", result.Name);
    }

    [Fact]
    public void WhenCollectionElementMapped_ThenForwardBrowseNameResolvesForElement()
    {
        // Arrange
        var fluent = new OpcUaFluentMapping<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>().Map(r => r.Motors, b => b.BrowseName("Motors"))
            .ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed"));
        var mappers = fluent.CreateMappers('.');
        var pathProviderMapper = (OpcUaPathProviderMapper)mappers[0];

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context)
        {
            Motors = [new OpcUaFluentMotor(context), new OpcUaFluentMotor(context)]
        };
        _ = root.TryGetRegisteredSubject()!;
        var speedProperty = root.Motors[1].TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        pathProviderMapper.TryGetMapping(speedProperty, root, out var mapping);

        // Assert - the browse name for the Speed property of a collection element resolves correctly.
        Assert.Equal("Speed", mapping!.BrowseName);
    }
}

[InterceptorSubject]
public partial class OpcUaFluentRoot
{
    public partial OpcUaFluentMotor Motor { get; set; }
    public partial List<OpcUaFluentMotor> Motors { get; set; }

    public OpcUaFluentRoot()
    {
        Motor = null!;
        Motors = [];
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
