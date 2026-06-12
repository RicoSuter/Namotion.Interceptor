using Moq;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class OpcUaPathProviderMapperTests
{
    [Fact]
    public void WhenPropertyIsIncluded_ThenReturnsBrowseName()
    {
        // Arrange - TestRoot.Name has [Path("opc", "Name")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("Name", config.BrowseName);
    }

    [Fact]
    public void WhenPropertyIsExcluded_ThenReturnsFalse()
    {
        // Arrange - TestNodeMapperModel.PlainProp has no [Path] attribute
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("PlainProp")!;

        // Act & Assert
        Assert.False(mapper.TryGetMapping(property, subject, out _));
    }

    [Fact]
    public void WhenDifferentConnectorName_ThenReturnsFalse()
    {
        // Arrange - TestRoot.Name has [Path("opc", "Name")] but we use "mqtt" provider
        var pathProvider = new AttributeBasedPathProvider("mqtt");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act & Assert
        Assert.False(mapper.TryGetMapping(property, subject, out _));
    }

    [Fact]
    public void WhenPathSegmentExists_ThenUsesItAsBrowseName()
    {
        // Arrange - TestRoot.Connected has [Path("opc", "Connected")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Connected")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("Connected", config.BrowseName);
    }

    [Fact]
    public void WhenNestedProperty_ThenReturnsBrowseName()
    {
        // Arrange - TestRoot.Person has [Path("opc", "Person")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Person")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("Person", config.BrowseName);
    }

    [Fact]
    public void WhenArrayProperty_ThenReturnsBrowseName()
    {
        // Arrange - TestRoot.ScalarNumbers has [Path("opc", "ScalarNumbers")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("ScalarNumbers")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("ScalarNumbers", config.BrowseName);
    }

    [Fact]
    public void WhenPropertyHasPathOnly_ThenDoesNotSetOtherFields()
    {
        // Arrange - PathProvider only sets BrowseName, nothing else
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert
        Assert.Equal("Name", config.BrowseName);
        Assert.Null(config.BrowseNamespaceUri);
        Assert.Null(config.NodeIdentifier);
        Assert.Null(config.TypeDefinition);
        Assert.Null(config.SamplingInterval);
        Assert.Null(config.QueueSize);
    }

    [Fact]
    public void WhenPropertyIsAttribute_ThenSetsReferenceTypeHasProperty()
    {
        // Arrange - Number_Unit is a PropertyAttribute (IsAttribute = true)
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        // Get the Number property and then its attribute
        var numberProperty = registeredSubject.TryGetProperty("Number")!;
        var attributeProperty = numberProperty.Attributes.FirstOrDefault(a => a.Name == "Number_Unit");
        Assert.NotNull(attributeProperty);

        // Act
        Assert.True(mapper.TryGetMapping(attributeProperty, subject, out var config));

        // Assert - Attributes get ReferenceType = "HasProperty"
        Assert.Equal("Unit", config.BrowseName);
        Assert.Equal("HasProperty", config.ReferenceType);
    }

    [Fact]
    public void WhenPropertyIsNotAttribute_ThenDoesNotSetReferenceType()
    {
        // Arrange - Name is a regular property (IsAttribute = false)
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        Assert.True(mapper.TryGetMapping(property, subject, out var config));

        // Assert - Non-attributes don't get a ReferenceType set
        Assert.Null(config.ReferenceType);
    }

    #region TryGetPropertyAsync Tests

    [Fact]
    public async Task WhenPropertyIsAttribute_ThenSkipsAttributes()
    {
        // Arrange - TryGetPropertyAsync should skip properties that are attributes
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        // Try to match the attribute's path segment (Unit)
        // TryGetPropertyAsync skips attributes, so it should return null
        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("Unit", 0),
            BrowseName = new QualifiedName("Unit", 0)
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object, registeredSubject.Subject), registeredSubject, CancellationToken.None);

        // Assert - Should be null because attributes are skipped in TryGetPropertyAsync
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenMatchingPathSegment_ThenReturnsProperty()
    {
        // Arrange - TestRoot.Name has [Path("opc", "Name")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
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
        var result = await mapper.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object, registeredSubject.Subject), registeredSubject, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Name", result.Name);
    }

    [Fact]
    public async Task WhenPropertyIsExcluded_ThenReturnsNull()
    {
        // Arrange - PlainProp has no [Path] attribute, so is excluded
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new OpcUaPathProviderMapper(pathProvider);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("PlainProp", 0),
            BrowseName = new QualifiedName("PlainProp", 0)
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, mockSession.Object, registeredSubject.Subject), registeredSubject, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    #endregion
}
