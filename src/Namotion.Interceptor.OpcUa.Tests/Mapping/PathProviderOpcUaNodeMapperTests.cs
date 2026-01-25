using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class PathProviderOpcUaNodeMapperTests
{
    [Fact]
    public void TryGetNodeConfiguration_WithIncludedProperty_ReturnsBrowseName()
    {
        // Arrange - TestRoot.Name has [Path("opc", "Name")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("Name", config.BrowseName);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithExcludedProperty_ReturnsNull()
    {
        // Arrange - TestNodeMapperModel.PlainProp has no [Path] attribute
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var subject = new TestNodeMapperModel(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("PlainProp")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithDifferentConnectorName_ReturnsNull()
    {
        // Arrange - TestRoot.Name has [Path("opc", "Name")] but we use "mqtt" provider
        var pathProvider = new AttributeBasedPathProvider("mqtt");
        var mapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public void TryGetNodeConfiguration_UsesPathSegmentAsBrowseName()
    {
        // Arrange - TestRoot.Connected has [Path("opc", "Connected")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Connected")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("Connected", config.BrowseName);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithNestedProperty_ReturnsBrowseName()
    {
        // Arrange - TestRoot.Person has [Path("opc", "Person")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Person")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("Person", config.BrowseName);
    }

    [Fact]
    public void TryGetNodeConfiguration_WithArrayProperty_ReturnsBrowseName()
    {
        // Arrange - TestRoot.ScalarNumbers has [Path("opc", "ScalarNumbers")]
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("ScalarNumbers")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("ScalarNumbers", config.BrowseName);
    }

    [Fact]
    public void TryGetNodeConfiguration_DoesNotSetOtherFields()
    {
        // Arrange - PathProvider only sets BrowseName, nothing else
        var pathProvider = new AttributeBasedPathProvider("opc");
        var mapper = new PathProviderOpcUaNodeMapper(pathProvider);
        var subject = new TestRoot(new InterceptorSubjectContext());
        var registeredSubject = new RegisteredSubject(subject);
        var property = registeredSubject.TryGetProperty("Name")!;

        // Act
        var config = mapper.TryGetNodeConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("Name", config.BrowseName);
        Assert.Null(config.BrowseNamespaceUri);
        Assert.Null(config.NodeIdentifier);
        Assert.Null(config.TypeDefinition);
        Assert.Null(config.SamplingInterval);
        Assert.Null(config.QueueSize);
    }
}
