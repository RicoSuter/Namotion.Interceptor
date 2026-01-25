using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class AttributeOpcUaNodeMapperTests
{
    [Fact]
    public void TryGetConfiguration_WithOpcUaNodeAttribute_ReturnsBrowseName()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("SimpleProp", config.BrowseName);
        Assert.Equal("http://test/", config.BrowseNamespaceUri);
    }

    [Fact]
    public void TryGetConfiguration_WithSamplingInterval_ReturnsSamplingInterval()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("MonitoredProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(500, config.SamplingInterval);
        Assert.Equal(10u, config.QueueSize);
    }

    [Fact]
    public void TryGetConfiguration_WithDataChangeFilter_ReturnsFilterSettings()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("FilteredProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(DataChangeTrigger.StatusValueTimestamp, config.DataChangeTrigger);
        Assert.Equal(DeadbandType.Absolute, config.DeadbandType);
        Assert.Equal(0.5, config.DeadbandValue);
    }

    [Fact]
    public void TryGetConfiguration_WithDiscardOldest_ReturnsDiscardOldest()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("QueueProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.False(config.DiscardOldest);
    }

    [Fact]
    public void TryGetConfiguration_WithTypeDefinition_ReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("TypedProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("AnalogItemType", config.TypeDefinition);
        Assert.Equal("http://opcfoundation.org/UA/", config.TypeDefinitionNamespace);
    }

    [Fact]
    public void TryGetConfiguration_WithOpcUaReferenceAttribute_ReturnsReferenceType()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("RefProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("HasComponent", config.ReferenceType);
    }

    [Fact]
    public void TryGetConfiguration_WithOpcUaValueAttribute_SetsIsValue()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("ValueProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.True(config.IsValue);
    }

    [Fact]
    public void TryGetConfiguration_WithModellingRule_ReturnsModellingRule()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("MandatoryProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(ModellingRule.Mandatory, config.ModellingRule);
    }

    [Fact]
    public void TryGetConfiguration_WithNodeClass_ReturnsNodeClass()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("VariableClassProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(OpcUaNodeClass.Variable, config.NodeClass);
    }

    [Fact]
    public void TryGetConfiguration_WithNoOpcUaAttributes_ReturnsNull()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("PlainProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public void TryGetConfiguration_DefaultSamplingInterval_ReturnsNull()
    {
        // Arrange - SimpleProp has OpcUaNode but no explicit SamplingInterval (uses default int.MinValue)
        var mapper = new AttributeOpcUaNodeMapper();
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("SimpleProp")!;

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Null(config.SamplingInterval); // Default (int.MinValue) should become null
    }
}
