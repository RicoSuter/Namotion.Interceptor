using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;

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
                .TypeDefinition("BaseDataVariableType")
                .TypeDefinitionNamespace("http://opcfoundation.org/UA/")
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
}
