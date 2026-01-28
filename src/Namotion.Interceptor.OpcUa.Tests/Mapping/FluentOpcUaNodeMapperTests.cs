using Moq;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class FluentOpcUaNodeMapperTests
{
    [Fact]
    public void TryGetNodeConfiguration_WithMappedProperty_ReturnsConfiguration()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p.BrowseName("CustomName"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("CustomName", config.BrowseName);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithUnmappedProperty_ReturnsNull()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p.BrowseName("CustomName"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Number")!; // Not mapped

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithMultipleProperties_ReturnsCorrectConfiguration()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p.BrowseName("NameNode").SamplingInterval(100))
            .Map(r => r.Number, p => p.BrowseName("NumberNode").SamplingInterval(500));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var nameProperty = registeredSubject.TryGetProperty("Name")!;
        var numberProperty = registeredSubject.TryGetProperty("Number")!;

        // Act
        var nameConfig = mapper.TryGetNodeConfiguration(nameProperty);
        var numberConfig = mapper.TryGetNodeConfiguration(numberProperty);

        // Assert
        Assert.NotNull(nameConfig);
        Assert.Equal("NameNode", nameConfig.BrowseName);
        Assert.Equal(100, nameConfig.SamplingInterval);

        Assert.NotNull(numberConfig);
        Assert.Equal("NumberNode", numberConfig.BrowseName);
        Assert.Equal(500, numberConfig.SamplingInterval);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithAllFluentMethods_ReturnsFullConfiguration()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("TestName")
                .BrowseNamespaceUri("http://test/")
                .NodeIdentifier("ns=2;s=TestName")
                .NodeNamespaceUri("http://test/")
                .DisplayName("Test Display Name")
                .Description("Test Description")
                .TypeDefinition("BaseDataVariableType", "http://opcfoundation.org/UA/")
                .NodeClass(OpcUaNodeClass.Variable)
                .DataType("String")
                .ReferenceType("HasComponent")
                .ItemReferenceType("HasComponent")
                .SamplingInterval(1000)
                .QueueSize(10)
                .DiscardOldest(false)
                .DataChangeTrigger(DataChangeTrigger.StatusValueTimestamp)
                .DeadbandType(DeadbandType.Absolute)
                .DeadbandValue(0.5)
                .ModellingRule(ModellingRule.Mandatory)
                .EventNotifier(1));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("TestName", config.BrowseName);
        Assert.Equal("http://test/", config.BrowseNamespaceUri);
        Assert.Equal("ns=2;s=TestName", config.NodeIdentifier);
        Assert.Equal("http://test/", config.NodeNamespaceUri);
        Assert.Equal("Test Display Name", config.DisplayName);
        Assert.Equal("Test Description", config.Description);
        Assert.Equal("BaseDataVariableType", config.TypeDefinition);
        Assert.Equal("http://opcfoundation.org/UA/", config.TypeDefinitionNamespace);
        Assert.Equal(OpcUaNodeClass.Variable, config.NodeClass);
        Assert.Equal("String", config.DataType);
        Assert.Equal("HasComponent", config.ReferenceType);
        Assert.Equal("HasComponent", config.ItemReferenceType);
        Assert.Equal(1000, config.SamplingInterval);
        Assert.Equal(10u, config.QueueSize);
        Assert.False(config.DiscardOldest);
        Assert.Equal(DataChangeTrigger.StatusValueTimestamp, config.DataChangeTrigger);
        Assert.Equal(DeadbandType.Absolute, config.DeadbandType);
        Assert.Equal(0.5, config.DeadbandValue);
        Assert.Equal(ModellingRule.Mandatory, config.ModellingRule);
        Assert.Equal((byte)1, config.EventNotifier);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithChainedCalls_UpdatesConfiguration()
    {
        // Arrange - Multiple fluent calls should update the same config
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("First")
                .SamplingInterval(100)
                .BrowseName("Second") // Should override "First"
                .QueueSize(5));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("Second", config.BrowseName); // Last call wins
        Assert.Equal(100, config.SamplingInterval);
        Assert.Equal(5u, config.QueueSize);
    }

    [Fact]
    public void Map_ReturnsMapperForChaining()
    {
        // Arrange & Act
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p.BrowseName("Name1"))
            .Map(r => r.Number, p => p.BrowseName("Name2"))
            .Map(r => r.Connected, p => p.BrowseName("Name3"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        // Assert - All properties should be mapped
        Assert.NotNull(mapper.TryGetNodeConfiguration(registeredSubject.TryGetProperty("Name")!));
        Assert.NotNull(mapper.TryGetNodeConfiguration(registeredSubject.TryGetProperty("Number")!));
        Assert.NotNull(mapper.TryGetNodeConfiguration(registeredSubject.TryGetProperty("Connected")!));
    }

    [Fact]
    public void TryGetNodeConfiguration_WithAdditionalReference_ReturnsReference()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("TestNode")
                .AdditionalReference("HasInterface", null, "i=17602"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.NotNull(config.AdditionalReferences);
        Assert.Single(config.AdditionalReferences);
        Assert.Equal("HasInterface", config.AdditionalReferences[0].ReferenceType);
        Assert.Equal("i=17602", config.AdditionalReferences[0].TargetNodeId);
        Assert.Null(config.AdditionalReferences[0].TargetNamespaceUri);
        Assert.True(config.AdditionalReferences[0].IsForward);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithMultipleAdditionalReferences_ReturnsAll()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("TestNode")
                .AdditionalReference("HasInterface", null, "i=17602")
                .AdditionalReference("GeneratesEvent", null, "ns=2;s=EventType", "http://test/", false));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.NotNull(config.AdditionalReferences);
        Assert.Equal(2, config.AdditionalReferences.Count);

        Assert.Equal("HasInterface", config.AdditionalReferences[0].ReferenceType);
        Assert.Equal("i=17602", config.AdditionalReferences[0].TargetNodeId);
        Assert.True(config.AdditionalReferences[0].IsForward);

        Assert.Equal("GeneratesEvent", config.AdditionalReferences[1].ReferenceType);
        Assert.Equal("ns=2;s=EventType", config.AdditionalReferences[1].TargetNodeId);
        Assert.Equal("http://test/", config.AdditionalReferences[1].TargetNamespaceUri);
        Assert.False(config.AdditionalReferences[1].IsForward);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithNestedMapping_ReturnsNestedConfiguration()
    {
        // Arrange - create test model with nested structure (TestRoot -> Person -> Address)
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var root = new TestRoot(context)
        {
            Person = new TestPerson(context)
            {
                Address = new TestAddress(context)
            }
        };

        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Person, person => person
                .BrowseName("MainPerson")
                .Map(p => p.Address!, address => address
                    .BrowseName("HomeAddress")
                    .Map(a => a.City, city => city.BrowseName("CityNode"))));

        // Get the nested property Person.Address.City
        var registeredSubject = root.TryGetRegisteredSubject()!;
        var personProperty = registeredSubject.Properties.First(p => p.Name == "Person");
        var personSubject = personProperty.Children.Single().Subject!.TryGetRegisteredSubject()!;
        var addressProperty = personSubject.Properties.First(p => p.Name == "Address");
        var addressSubject = addressProperty.Children.Single().Subject!.TryGetRegisteredSubject()!;
        var cityProperty = addressSubject.Properties.First(p => p.Name == "City");

        // Act
        var personConfig = mapper.TryGetNodeConfiguration(personProperty);
        var addressConfig = mapper.TryGetNodeConfiguration(addressProperty);
        var cityConfig = mapper.TryGetNodeConfiguration(cityProperty);

        // Assert
        Assert.NotNull(personConfig);
        Assert.Equal("MainPerson", personConfig.BrowseName);

        Assert.NotNull(addressConfig);
        Assert.Equal("HomeAddress", addressConfig.BrowseName);

        Assert.NotNull(cityConfig);
        Assert.Equal("CityNode", cityConfig.BrowseName);
    }

    #region Namespace Parameter Tests

    [Fact]
    public void TypeDefinition_WithNamespace_SetsConfiguration()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("Device")
                .TypeDefinition("DeviceType", "http://opcfoundation.org/UA/DI/"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("DeviceType", config.TypeDefinition);
        Assert.Equal("http://opcfoundation.org/UA/DI/", config.TypeDefinitionNamespace);
    }

    [Fact]
    public void TypeDefinition_WithoutNamespace_SetsOnlyIdentifier()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("Folder")
                .TypeDefinition("FolderType"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("FolderType", config.TypeDefinition);
        Assert.Null(config.TypeDefinitionNamespace);
    }

    [Fact]
    public void ReferenceType_WithNamespace_SetsConfiguration()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("Device")
                .ReferenceType("HasDevice", "http://opcfoundation.org/UA/DI/"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("HasDevice", config.ReferenceType);
        Assert.Equal("http://opcfoundation.org/UA/DI/", config.ReferenceTypeNamespace);
    }

    [Fact]
    public void DataType_WithNamespace_SetsConfiguration()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("Temperature")
                .DataType("TemperatureType", "http://example.com/types/"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("TemperatureType", config.DataType);
        Assert.Equal("http://example.com/types/", config.DataTypeNamespace);
    }

    [Fact]
    public void ItemReferenceType_WithNamespace_SetsConfiguration()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p
                .BrowseName("Devices")
                .ItemReferenceType("ContainsDevice", "http://example.com/types/"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("ContainsDevice", config.ItemReferenceType);
        Assert.Equal("http://example.com/types/", config.ItemReferenceTypeNamespace);
    }

    #endregion

    #region TryGetPropertyAsync Tests

    [Fact]
    public async Task TryGetPropertyAsync_WithMatchingBrowseName_ReturnsProperty()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p.BrowseName("CustomName"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("TestNodeId", 0),
            BrowseName = new QualifiedName("CustomName", 0)
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Name", result.Name);
    }

    [Fact]
    public async Task TryGetPropertyAsync_WithUnmappedProperty_ReturnsNull()
    {
        // Arrange
        var mapper = new FluentOpcUaNodeMapper<TestRoot>()
            .Map(r => r.Name, p => p.BrowseName("CustomName"));

        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);

        var namespaceUris = new NamespaceTable();
        var mockSession = new Mock<ISession>();
        mockSession.Setup(s => s.NamespaceUris).Returns(namespaceUris);

        // Looking for a property that's not mapped
        var nodeReference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId("TestNodeId", 0),
            BrowseName = new QualifiedName("Number", 0) // Number is not mapped
        };

        // Act
        var result = await mapper.TryGetPropertyAsync(registeredSubject, nodeReference, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    #endregion
}
