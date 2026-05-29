using Moq;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class OpcUaCompositeMapperTests
{
    [Fact]
    public void WhenNoMappers_ThenReturnsFalse()
    {
        // Arrange
        var composite = new OpcUaCompositeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act & Assert
        Assert.False(composite.TryGetMapping(property, subject, out _));
    }

    [Fact]
    public void WhenSingleMapper_ThenReturnsMapperConfig()
    {
        // Arrange
        var attributeMapper = new OpcUaAttributeMapper();
        var composite = new OpcUaCompositeMapper(attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        Assert.True(composite.TryGetMapping(property, subject, out var result));

        // Assert
        Assert.Equal("SimpleProp", result.BrowseName);
    }

    [Fact]
    public void WhenMultipleMappers_ThenLastMapperWins()
    {
        // Arrange - PathProvider gives BrowseName from Path attribute, Attribute mapper gives from OpcUaNode
        // Using MonitoredProp which has [OpcUaNode("MonitoredProp", null, SamplingInterval = 500, QueueSize = 10)]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new OpcUaPathProviderMapper(pathProvider);
        var attributeMapper = new OpcUaAttributeMapper();

        // Attribute mapper is second, so it wins for overlapping fields
        var composite = new OpcUaCompositeMapper(pathMapper, attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("MonitoredProp")!;

        // Act
        Assert.True(composite.TryGetMapping(property, subject, out var result));

        // Assert
        Assert.Equal("MonitoredProp", result.BrowseName); // From attribute mapper (last wins)
        Assert.Equal(500, result.SamplingInterval); // From attribute mapper
    }

    [Fact]
    public void WhenMultipleMappers_ThenMergesFields()
    {
        // Arrange - Using property with both path and OpcUa attributes
        // SimpleProp: [OpcUaNode("SimpleProp", "http://test/")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new OpcUaPathProviderMapper(pathProvider);
        var attributeMapper = new OpcUaAttributeMapper();

        var composite = new OpcUaCompositeMapper(pathMapper, attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        Assert.True(composite.TryGetMapping(property, subject, out var result));

        // Assert
        Assert.Equal("SimpleProp", result.BrowseName);
        Assert.Equal("http://test/", result.BrowseNamespaceUri);
    }

    [Fact]
    public void WhenAllMappersReturnNull_ThenReturnsFalse()
    {
        // Arrange - PlainProp has no OpcUaNode attribute, so AttributeMapper returns null
        // but PathProvider should still work if we had a Path attribute
        var attributeMapper = new OpcUaAttributeMapper();
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new OpcUaPathProviderMapper(pathProvider);

        var composite = new OpcUaCompositeMapper(attributeMapper, pathMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        // PlainProp has no Path or OpcUaNode attributes, so both return null
        var property = registeredSubject.TryGetProperty("PlainProp")!;

        // Act & Assert - Both mappers return null for this property
        Assert.False(composite.TryGetMapping(property, subject, out _));
    }

    [Fact]
    public void WhenAttributeAndPathMappersCombined_ThenCombinesConfiguration()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new OpcUaPathProviderMapper(pathProvider);
        var attributeMapper = new OpcUaAttributeMapper();

        // PathMapper first, then AttributeMapper (AttributeMapper wins for overlapping)
        var composite = new OpcUaCompositeMapper(pathMapper, attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        // MonitoredProp has both OpcUaNode with sampling settings
        var property = registeredSubject.TryGetProperty("MonitoredProp")!;

        // Act
        Assert.True(composite.TryGetMapping(property, subject, out var result));

        // Assert
        Assert.Equal("MonitoredProp", result.BrowseName);
        Assert.Equal(500, result.SamplingInterval);
        Assert.Equal(10u, result.QueueSize);
    }

    [Fact]
    public void WhenThreeOrMoreMappers_ThenLastMapperWins()
    {
        // Arrange - Create three mappers to verify 3+ mapper behavior
        var pathProvider1 = new AttributeBasedPathProvider("opc");
        var pathMapper1 = new OpcUaPathProviderMapper(pathProvider1);

        var pathProvider2 = new AttributeBasedPathProvider("opc");
        var pathMapper2 = new OpcUaPathProviderMapper(pathProvider2);

        var attributeMapper = new OpcUaAttributeMapper();

        // Three mappers: pathMapper1, pathMapper2, attributeMapper (last wins)
        var composite = new OpcUaCompositeMapper(pathMapper1, pathMapper2, attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        // FilteredProp has OpcUaNode with filter settings
        var property = registeredSubject.TryGetProperty("FilteredProp")!;

        // Act
        Assert.True(composite.TryGetMapping(property, subject, out var result));

        // Assert - Verifies that all three mappers are processed correctly
        Assert.Equal("FilteredProp", result.BrowseName);
        // These come from attributeMapper (last mapper)
        Assert.Equal(DataChangeTrigger.StatusValueTimestamp, result.DataChangeTrigger);
        Assert.Equal(DeadbandType.Absolute, result.DeadbandType);
        Assert.Equal(0.5, result.DeadbandValue);
    }

    [Fact]
    public async Task WhenThreeOrMoreMappersLookupProperty_ThenLastMatchingMapperWins()
    {
        // Arrange - Create three mappers
        var pathProvider1 = new AttributeBasedPathProvider("opc");
        var pathMapper1 = new OpcUaPathProviderMapper(pathProvider1);

        var pathProvider2 = new AttributeBasedPathProvider("opc");
        var pathMapper2 = new OpcUaPathProviderMapper(pathProvider2);

        var attributeMapper = new OpcUaAttributeMapper();

        var composite = new OpcUaCompositeMapper(pathMapper1, pathMapper2, attributeMapper);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("Name", 0),
            BrowseName = new QualifiedName("Name", 0)
        };

        // Act
        var result = await composite.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object, registeredSubject.Subject), registeredSubject, CancellationToken.None);

        // Assert - Should find the property through the composite (last matching mapper wins)
        Assert.NotNull(result);
        Assert.Equal("Name", result.Name);
    }

    #region TryGetPropertyAsync Tests

    [Fact]
    public async Task WhenLookupPropertyWithMultipleMappers_ThenLastMapperWins()
    {
        // Arrange - Create two mappers that both match the same property
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new OpcUaPathProviderMapper(pathProvider);
        var attributeMapper = new OpcUaAttributeMapper();

        // AttributeMapper is last, so it wins
        var composite = new OpcUaCompositeMapper(pathMapper, attributeMapper);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        // Name property has [Path("opc", "Name")] so PathMapper matches
        // But also likely matches by AttributeMapper browse name
        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("Name", 0),
            BrowseName = new QualifiedName("Name", 0)
        };

        // Act
        var result = await composite.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object, registeredSubject.Subject), registeredSubject, CancellationToken.None);

        // Assert - Should find the property (last mapper that matches wins)
        Assert.NotNull(result);
        Assert.Equal("Name", result.Name);
    }

    [Fact]
    public async Task WhenNoPropertyMatches_ThenReturnsNull()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new OpcUaPathProviderMapper(pathProvider);
        var attributeMapper = new OpcUaAttributeMapper();

        var composite = new OpcUaCompositeMapper(pathMapper, attributeMapper);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("NonExistent", 0),
            BrowseName = new QualifiedName("NonExistent", 0)
        };

        // Act
        var result = await composite.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object, registeredSubject.Subject), registeredSubject, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    #endregion
}
