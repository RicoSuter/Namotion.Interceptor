using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Paths;

public class FluentPathProviderTests
{
    private sealed record Meta;

    [Fact]
    public void WhenPropertyRegistered_ThenIncludedAndSegmentReturned()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(FluentPathTestSensor), "Temperature", "temp", new Meta());
        var provider = new FluentPathProvider(registry, '/');

        var subject = new FluentPathTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var included = provider.IsPropertyIncluded(property);
        var segment = provider.TryGetPropertySegment(property);

        // Assert
        Assert.True(included);
        Assert.Equal("temp", segment);
    }

    [Fact]
    public void WhenRegisteredWithoutSegment_ThenFallsBackToBrowseName()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(FluentPathTestSensor), "Temperature", segment: null, new Meta());
        var provider = new FluentPathProvider(registry);

        var subject = new FluentPathTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var segment = provider.TryGetPropertySegment(property);

        // Assert
        Assert.Equal("Temperature", segment);
    }

    [Fact]
    public void WhenPropertyNotRegistered_ThenExcludedAndNullSegment()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        var provider = new FluentPathProvider(registry);

        var subject = new FluentPathTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var included = provider.IsPropertyIncluded(property);
        var segment = provider.TryGetPropertySegment(property);

        // Assert
        Assert.False(included);
        Assert.Null(segment);
    }

    [Fact]
    public void WhenSeparatorConfigured_ThenExposed()
    {
        // Arrange
        var provider = new FluentPathProvider(new FluentMappingRegistry<Meta>(), '/');

        // Act & Assert
        Assert.Equal('/', provider.PathSeparator);
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class FluentPathTestSensor
{
    public partial double Temperature { get; set; }

    public FluentPathTestSensor()
    {
        Temperature = 0;
    }
}
