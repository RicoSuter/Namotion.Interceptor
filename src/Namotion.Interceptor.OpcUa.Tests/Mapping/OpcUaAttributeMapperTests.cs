using Moq;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class OpcUaAttributeMapperTests
{
    [Fact]
    public void WhenPropertyHasOpcUaNodeAttribute_ThenReturnsBrowseName()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("SimpleProp", config.BrowseName);
        Assert.Equal("http://test/", config.BrowseNamespaceUri);
    }

    [Fact]
    public void WhenPropertyHasSamplingInterval_ThenReturnsSamplingInterval()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("MonitoredProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal(500, config.SamplingInterval);
        Assert.Equal(10u, config.QueueSize);
    }

    [Fact]
    public void WhenPropertyHasDataChangeFilter_ThenReturnsFilterSettings()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("FilteredProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal(DataChangeTrigger.StatusValueTimestamp, config.DataChangeTrigger);
        Assert.Equal(DeadbandType.Absolute, config.DeadbandType);
        Assert.Equal(0.5, config.DeadbandValue);
    }

    [Fact]
    public void WhenPropertyHasDiscardOldest_ThenReturnsDiscardOldest()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("QueueProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.False(config.DiscardOldest);
    }

    [Fact]
    public void WhenPropertyHasTypeDefinition_ThenReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("TypedProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("AnalogItemType", config.TypeDefinition);
        Assert.Equal("http://opcfoundation.org/UA/", config.TypeDefinitionNamespace);
    }

    [Fact]
    public void WhenPropertyHasOpcUaReferenceAttribute_ThenReturnsReferenceType()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("RefProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("HasComponent", config.ReferenceType);
    }

    [Fact]
    public void WhenPropertyHasOpcUaValueAttribute_ThenSetsIsValue()
    {
        // Arrange - use AttributeMapperVariableChild which has [OpcUaNode(NodeClass = OpcUaNodeClass.Variable)]
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperVariableChild(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Value")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.True(config.IsValue);
    }

    [Fact]
    public void WhenPropertyHasModellingRule_ThenReturnsModellingRule()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("MandatoryProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal(ModellingRule.Mandatory, config.ModellingRule);
    }

    [Fact]
    public void WhenPropertyHasNodeClass_ThenReturnsNodeClass()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("VariableClassProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal(OpcUaNodeClass.Variable, config.NodeClass);
    }

    [Fact]
    public void WhenPropertyHasNoOpcUaAttributes_ThenReturnsFalse()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("PlainProp")!;

        // Act & Assert
        Assert.False(mapper.TryGetMapping(property, subject, out _));
    }

    [Fact]
    public void WhenSamplingIntervalIsDefault_ThenReturnsNull()
    {
        // Arrange - SimpleProp has OpcUaNode but no explicit SamplingInterval (uses default int.MinValue)
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Null(config.SamplingInterval); // Default (int.MinValue) should become null
    }

    [Fact]
    public void WhenEventNotifierIsZero_ThenReturnsZero()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("EventNotifierZeroProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal((byte)0, config.EventNotifier);
    }

    [Fact]
    public void WhenOpcUaValueWithoutNodeClassVariable_ThenThrowsException()
    {
        // Arrange - property with [OpcUaValue] but class doesn't have NodeClass = Variable
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperInvalidValueModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("InvalidValue")!;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => mapper.TryGetMapping(property, subject, out _));
        Assert.Contains("OpcUaValue", exception.Message);
        Assert.Contains("NodeClass", exception.Message);
    }

    [Fact]
    public void WhenCollectionHasClassLevelAttribute_ThenReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperCollectionParent(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Items")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("CollectionItemType", config.TypeDefinition);
    }

    [Fact]
    public void WhenDictionaryHasClassLevelAttribute_ThenReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperCollectionParent(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("ItemsByKey")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("CollectionItemType", config.TypeDefinition);
    }

    [Fact]
    public void WhenArrayHasClassLevelAttribute_ThenReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperCollectionParent(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("ItemsArray")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("CollectionItemType", config.TypeDefinition);
    }

    #region TryGetPropertyAsync Tests

    [Fact]
    public async Task WhenMatchingNodeIdentifier_ThenReturnsProperty()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
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
        var result = await mapper.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object), registeredSubject, CancellationToken.None);

        // Assert - Should match via NodeIdentifier (Priority 1), not BrowseName (Priority 2)
        Assert.NotNull(result);
        Assert.Equal("NodeIdProp", result.Name);
    }

    [Fact]
    public async Task WhenMatchingBrowseName_ThenReturnsProperty()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
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
        var result = await mapper.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object), registeredSubject, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SimpleProp", result.Name);
    }

    [Fact]
    public async Task WhenNamespaceDoesNotMatch_ThenReturnsNull()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
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
        var result = await mapper.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object), registeredSubject, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenNoPropertyMatches_ThenReturnsNull()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
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
        var result = await mapper.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object), registeredSubject, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenDefaultNamespaceUriIsSet_ThenUsesDefault()
    {
        // Arrange - use default namespace that matches the node
        var mapper = new OpcUaAttributeMapper(defaultNamespaceUri: "http://default/");
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
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
        var result = await mapper.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object), registeredSubject, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("MonitoredProp", result.Name);
    }

    #endregion

    #region Namespace Property Mapping Tests

    [Fact]
    public void WhenPropertyHasDataTypeNamespace_ThenReturnsDataTypeNamespace()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("DataTypeProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("CustomDataType", config.DataType);
        Assert.Equal("http://custom/datatypes/", config.DataTypeNamespace);
    }

    [Fact]
    public void WhenReferenceAttributeHasNamespace_ThenReturnsReferenceTypeNamespace()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("RefAttrProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("HasComponent", config.ReferenceType);
        Assert.Equal("http://custom/reftypes/", config.ReferenceTypeNamespace);
    }

    [Fact]
    public void WhenPropertyHasItemReferenceTypeNamespace_ThenReturnsItemReferenceTypeNamespace()
    {
        // Arrange
        var mapper = new OpcUaAttributeMapper();
        var subject = new AttributeMapperTestModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("CollectionProp")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("CustomItemRef", config.ItemReferenceType);
        Assert.Equal("http://custom/itemrefs/", config.ItemReferenceTypeNamespace);
    }

    #endregion
}
