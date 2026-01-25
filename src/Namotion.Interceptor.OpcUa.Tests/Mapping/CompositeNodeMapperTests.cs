using Moq;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class CompositeNodeMapperTests
{
    [Fact]
    public void TryGetNodeConfiguration_WithNoMappers_ReturnsNull()
    {
        // Arrange
        var composite = new CompositeNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        var result = composite.TryGetNodeConfiguration(property);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithSingleMapper_ReturnsMapperConfig()
    {
        // Arrange
        var attributeMapper = new AttributeOpcUaNodeMapper();
        var composite = new CompositeNodeMapper(attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        var result = composite.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SimpleProp", result.BrowseName);
    }

    [Fact]
    public void TryGetNodeConfiguration_LastMapperWins()
    {
        // Arrange - PathProvider gives BrowseName from Path attribute, Attribute mapper gives from OpcUaNode
        // Using MonitoredProp which has [OpcUaNode("MonitoredProp", null, SamplingInterval = 500, QueueSize = 10)]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var attributeMapper = new AttributeOpcUaNodeMapper();

        // Attribute mapper is second, so it wins for overlapping fields
        var composite = new CompositeNodeMapper(pathMapper, attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("MonitoredProp")!;

        // Act
        var result = composite.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("MonitoredProp", result.BrowseName); // From attribute mapper (last wins)
        Assert.Equal(500, result.SamplingInterval); // From attribute mapper
    }

    [Fact]
    public void TryGetNodeConfiguration_MergesFieldsFromMultipleMappers()
    {
        // Arrange - Using property with both path and OpcUa attributes
        // SimpleProp: [OpcUaNode("SimpleProp", "http://test/")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var attributeMapper = new AttributeOpcUaNodeMapper();

        var composite = new CompositeNodeMapper(pathMapper, attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        var result = composite.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SimpleProp", result.BrowseName);
        Assert.Equal("http://test/", result.BrowseNamespaceUri);
    }

    [Fact]
    public void TryGetNodeConfiguration_SkipsNullResults()
    {
        // Arrange - PlainProp has no OpcUaNode attribute, so AttributeMapper returns null
        // but PathProvider should still work if we had a Path attribute
        var attributeMapper = new AttributeOpcUaNodeMapper();
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new PathProviderOpcUaNodeMapper(pathProvider);

        var composite = new CompositeNodeMapper(attributeMapper, pathMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        // PlainProp has no Path or OpcUaNode attributes, so both return null
        var property = registeredSubject.TryGetProperty("PlainProp")!;

        // Act
        var result = composite.TryGetNodeConfiguration(property);

        // Assert - Both mappers return null for this property
        Assert.Null(result);
    }

    [Fact]
    public void TryGetNodeConfiguration_AttributeMapperWithPathMapper_CombinesConfiguration()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var attributeMapper = new AttributeOpcUaNodeMapper();

        // PathMapper first, then AttributeMapper (AttributeMapper wins for overlapping)
        var composite = new CompositeNodeMapper(pathMapper, attributeMapper);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        // MonitoredProp has both OpcUaNode with sampling settings
        var property = registeredSubject.TryGetProperty("MonitoredProp")!;

        // Act
        var result = composite.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("MonitoredProp", result.BrowseName);
        Assert.Equal(500, result.SamplingInterval);
        Assert.Equal(10u, result.QueueSize);
    }

    #region TryGetPropertyAsync Tests

    [Fact]
    public async Task TryGetPropertyAsync_LastMapperWins()
    {
        // Arrange - Create two mappers that both match the same property
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var attributeMapper = new AttributeOpcUaNodeMapper();

        // AttributeMapper is last, so it wins
        var composite = new CompositeNodeMapper(pathMapper, attributeMapper);
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
        var result = await composite.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert - Should find the property (last mapper that matches wins)
        Assert.NotNull(result);
        Assert.Equal("Name", result.Name);
    }

    [Fact]
    public async Task TryGetPropertyAsync_WithNoMatch_ReturnsNull()
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("opc");
        var pathMapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var attributeMapper = new AttributeOpcUaNodeMapper();

        var composite = new CompositeNodeMapper(pathMapper, attributeMapper);
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
        var result = await composite.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    #endregion
}
