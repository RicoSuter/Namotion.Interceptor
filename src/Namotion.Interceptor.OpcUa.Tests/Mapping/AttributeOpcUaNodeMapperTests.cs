using Moq;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class AttributeOpcUaNodeMapperTests
{
    [Fact]
    public void TryGetNodeConfiguration_WithOpcUaNodeAttribute_ReturnsBrowseName()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("SimpleProp", config.BrowseName);
        Assert.Equal("http://test/", config.BrowseNamespaceUri);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithSamplingInterval_ReturnsSamplingInterval()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("MonitoredProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(500, config.SamplingInterval);
        Assert.Equal(10u, config.QueueSize);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithDataChangeFilter_ReturnsFilterSettings()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("FilteredProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(DataChangeTrigger.StatusValueTimestamp, config.DataChangeTrigger);
        Assert.Equal(DeadbandType.Absolute, config.DeadbandType);
        Assert.Equal(0.5, config.DeadbandValue);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithDiscardOldest_ReturnsDiscardOldest()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("QueueProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.False(config.DiscardOldest);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithTypeDefinition_ReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("TypedProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("AnalogItemType", config.TypeDefinition);
        Assert.Equal("http://opcfoundation.org/UA/", config.TypeDefinitionNamespace);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithOpcUaReferenceAttribute_ReturnsReferenceType()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("RefProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("HasComponent", config.ReferenceType);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithOpcUaValueAttribute_SetsIsValue()
    {
        // Arrange - use TestVariableChild which has [OpcUaNode(NodeClass = OpcUaNodeClass.Variable)]
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestVariableChild(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Value")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.True(config.IsValue);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithModellingRule_ReturnsModellingRule()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("MandatoryProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(ModellingRule.Mandatory, config.ModellingRule);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithNodeClass_ReturnsNodeClass()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("VariableClassProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(OpcUaNodeClass.Variable, config.NodeClass);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithNoOpcUaAttributes_ReturnsNull()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("PlainProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public void TryGetNodeConfiguration_DefaultSamplingInterval_ReturnsNull()
    {
        // Arrange - SimpleProp has OpcUaNode but no explicit SamplingInterval (uses default int.MinValue)
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Null(config.SamplingInterval); // Default (int.MinValue) should become null
    }

    [Fact]
    public void TryGetNodeConfiguration_WithEventNotifierZero_ReturnsZero()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("EventNotifierZeroProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal((byte)0, config.EventNotifier);
    }

    [Fact]
    public void TryGetNodeConfiguration_OpcUaValueWithoutNodeClassVariable_ThrowsException()
    {
        // Arrange - property with [OpcUaValue] but class doesn't have NodeClass = Variable
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestInvalidOpcUaValueModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("InvalidValue")!;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => mapper.TryGetNodeConfiguration(property));
        Assert.Contains("OpcUaValue", exception.Message);
        Assert.Contains("NodeClass", exception.Message);
    }

    [Fact]
    public void TryGetNodeConfiguration_CollectionWithClassLevelAttribute_ReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestCollectionParent(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Items")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("CollectionItemType", config.TypeDefinition);
    }

    [Fact]
    public void TryGetNodeConfiguration_DictionaryWithClassLevelAttribute_ReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestCollectionParent(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("ItemsByKey")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("CollectionItemType", config.TypeDefinition);
    }

    [Fact]
    public void TryGetNodeConfiguration_ArrayWithClassLevelAttribute_ReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestCollectionParent(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("ItemsArray")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("CollectionItemType", config.TypeDefinition);
    }

    #region TryGetPropertyAsync Tests

    [Fact]
    public async Task TryGetPropertyAsync_WithMatchingNodeIdentifier_ReturnsProperty()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        namespaceUris.Append("http://myserver/");
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        // NodeIdProp has [OpcUaNode("NodeIdProp", null, NodeIdentifier = "ns=2;s=MyExplicitNodeId", NodeNamespaceUri = "http://myserver/")]
        var nodeReference = new ReferenceDescription
        {
            // Node ID matches the NodeIdentifier (Priority 1 matching)
            NodeId = new ExpandedNodeId("ns=2;s=MyExplicitNodeId", "http://myserver/"),
            BrowseName = new QualifiedName("SomeOtherBrowseName", 0) // BrowseName doesn't match
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert - Should match via NodeIdentifier (Priority 1), not BrowseName (Priority 2)
        Assert.NotNull(result);
        Assert.Equal("NodeIdProp", result.Name);
    }

    [Fact]
    public async Task TryGetPropertyAsync_WithMatchingBrowseName_ReturnsProperty()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        namespaceUris.Append("http://test/");
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("TestNodeId", 1),
            BrowseName = new QualifiedName("SimpleProp", 1) // Matches [OpcUaNode("SimpleProp", "http://test/")]
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SimpleProp", result.Name);
    }

    [Fact]
    public async Task TryGetPropertyAsync_WithNonMatchingNamespace_ReturnsNull()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        namespaceUris.Append("http://test/");
        namespaceUris.Append("http://other/");
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("TestNodeId", 2),
            BrowseName = new QualifiedName("SimpleProp", 2) // Wrong namespace index
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPropertyAsync_WithNoMatch_ReturnsNull()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("UnknownNode", 0),
            BrowseName = new QualifiedName("NonExistentProperty", 0)
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPropertyAsync_WithDefaultNamespaceUri_UsesDefault()
    {
        // Arrange - use default namespace that matches the node
        var mapper = new AttributeOpcUaNodeMapper(defaultNamespaceUri: "http://default/");
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        namespaceUris.Append("http://default/");
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        // MonitoredProp has no explicit namespace in attribute, so defaultNamespaceUri should be used
        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("MonitoredProp", 1),
            BrowseName = new QualifiedName("MonitoredProp", 0)
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("MonitoredProp", result.Name);
    }

    #endregion
}
