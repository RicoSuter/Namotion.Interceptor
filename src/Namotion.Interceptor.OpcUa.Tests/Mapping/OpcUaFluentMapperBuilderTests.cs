using Moq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class OpcUaFluentMapperBuilderTests
{
    [Fact]
    public void WhenLeafMapped_ThenBrowseNameIsSegmentAndMetadata()
    {
        // Arrange
        var fluent = new OpcUaFluentMapperBuilder<OpcUaFluentRoot>();
        fluent.ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed").SamplingInterval(500));
        var mapper = fluent.Build('.');

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var speed = root.Motor.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = mapper.TryGetMapping(speed, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("Speed", mapping!.BrowseName);
        Assert.Equal(500, mapping.SamplingInterval);
    }

    [Fact]
    public void WhenTypeReusedAcrossLocations_ThenBrowseNameResolvesEverywhere()
    {
        // Arrange
        var fluent = new OpcUaFluentMapperBuilder<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>()
                .Map(r => r.Motor, b => b.BrowseName("Motor"))
            .ForType<OpcUaFluentMotor>()
                .Map(m => m.Speed, b => b.BrowseName("Speed"));
        var mapper = fluent.Build('.');

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var motorProperty = root.TryGetRegisteredSubject()!.TryGetProperty("Motor")!;
        var speedProperty = root.Motor.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        mapper.TryGetMapping(motorProperty, root, out var motorMapping);
        mapper.TryGetMapping(speedProperty, root, out var speedMapping);

        // Assert - the fluent mapper supplies the browse name from the fluent segment.
        Assert.Equal("Motor", motorMapping!.BrowseName);
        Assert.Equal("Speed", speedMapping!.BrowseName);
    }

    [Fact]
    public void WhenConfigureUsedForReferencedType_ThenTypeSelfMergesIntoSubjectMember()
    {
        // Arrange
        var fluent = new OpcUaFluentMapperBuilder<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>()
                .Map(r => r.Motor, b => b.BrowseName("Motor"))
            .ForType<OpcUaFluentMotor>()
                .Configure(b => b.TypeDefinition("MotorType"));
        var mapper = fluent.Build('.');

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var motorProperty = root.TryGetRegisteredSubject()!.TryGetProperty("Motor")!;

        // Act
        var found = mapper.TryGetMapping(motorProperty, root, out var mapping);

        // Assert - the Motor member's metadata (BrowseName) plus its type-self TypeDefinition.
        Assert.True(found);
        Assert.Equal("Motor", mapping!.BrowseName);
        Assert.Equal("MotorType", mapping.TypeDefinition);
    }

    [Fact]
    public async Task WhenFluentBrowseNameConfigured_ThenReverseLookupResolvesSpeedProperty()
    {
        // Arrange
        var fluent = new OpcUaFluentMapperBuilder<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>().Map(r => r.Motor, b => b.BrowseName("Motor"))
            .ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed"));
        var mapper = fluent.Build('.');

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
        var result = await mapper.TryGetPropertyAsync(
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
        var fluent = new OpcUaFluentMapperBuilder<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>().Map(r => r.Motors, b => b.BrowseName("Motors"))
            .ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed"));
        var mapper = fluent.Build('.');

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context)
        {
            Motors = [new OpcUaFluentMotor(context), new OpcUaFluentMotor(context)]
        };
        _ = root.TryGetRegisteredSubject()!;
        var speedProperty = root.Motors[1].TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        mapper.TryGetMapping(speedProperty, root, out var mapping);

        // Assert - the browse name for the Speed property of a collection element resolves correctly.
        Assert.Equal("Speed", mapping!.BrowseName);
    }

    [Fact]
    public async Task WhenCollectionElementMapped_ThenReverseLookupResolvesSpeedForElement()
    {
        // Arrange
        var fluent = new OpcUaFluentMapperBuilder<OpcUaFluentRoot>();
        fluent
            .ForType<OpcUaFluentRoot>().Map(r => r.Motors, b => b.BrowseName("Motors"))
            .ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed"));
        var mapper = fluent.Build('.');

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context)
        {
            Motors = [new OpcUaFluentMotor(context), new OpcUaFluentMotor(context)]
        };
        _ = root.TryGetRegisteredSubject()!;
        var elementRegisteredSubject = root.Motors[1].TryGetRegisteredSubject()!;

        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(new NamespaceTable());

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("Speed", 0),
            BrowseName = new QualifiedName("Speed", 0)
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(
            new OpcUaLookupKey(nodeReference, mockSession.Object, root),
            elementRegisteredSubject,
            CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Speed", result.Name);
    }

    [Fact]
    public void WhenBuildCalledOffTypeBuilder_ThenProducesWorkingMapper()
    {
        // Arrange - the single-expression form ends the chain in Build() on the type builder.
        var mapper = new OpcUaFluentMapperBuilder<OpcUaFluentRoot>()
            .ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b.BrowseName("Speed").SamplingInterval(250))
            .Build('.');

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var speed = root.Motor.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = mapper.TryGetMapping(speed, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("Speed", mapping!.BrowseName);
        Assert.Equal(250, mapping.SamplingInterval);
    }

    [Fact]
    public void WhenDataTypeAndAdditionalReferenceConfigured_ThenMetadataIsPopulated()
    {
        // Arrange
        var fluent = new OpcUaFluentMapperBuilder<OpcUaFluentRoot>();
        fluent.ForType<OpcUaFluentMotor>().Map(m => m.Speed, b => b
            .BrowseName("Speed")
            .DataType("Double", "http://example/types")
            .AdditionalReference("HasInterface", null, "i=17603"));
        var mapper = fluent.Build('.');

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new OpcUaFluentRoot(context) { Motor = new OpcUaFluentMotor(context) };
        _ = root.TryGetRegisteredSubject()!;
        var speed = root.Motor.TryGetRegisteredSubject()!.TryGetProperty("Speed")!;

        // Act
        var found = mapper.TryGetMapping(speed, root, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal("Double", mapping!.DataType);
        Assert.Equal("http://example/types", mapping.DataTypeNamespace);
        var reference = Assert.Single(mapping.AdditionalReferences!);
        Assert.Equal("HasInterface", reference.ReferenceType);
        Assert.Equal("i=17603", reference.TargetNodeId);
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
