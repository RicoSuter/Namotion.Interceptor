using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
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
        registry.AddPropertyMetadata(typeof(FluentPathTestSensor), "Temperature", "temp", new Meta());
        var provider = new FluentPathProvider<Meta>(registry, '/');

        var subject = new FluentPathTestSensor(InterceptorSubjectContext.Create());
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
        registry.AddPropertyMetadata(typeof(FluentPathTestSensor), "Temperature", segment: null, new Meta());
        var provider = new FluentPathProvider<Meta>(registry);

        var subject = new FluentPathTestSensor(InterceptorSubjectContext.Create());
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
        var provider = new FluentPathProvider<Meta>(registry);

        var subject = new FluentPathTestSensor(InterceptorSubjectContext.Create());
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
    public void WhenInclusionAndSegmentAreAskedInOneCall_ThenTheyAgreeWithIsPropertyIncludedAndTryGetPropertySegment()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddPropertyMetadata(typeof(Person), nameof(Person.FirstName), "first", new Meta());
        registry.AddPropertyMetadata(typeof(Person), nameof(Person.LastName), segment: null, new Meta());
        var provider = new FluentPathProvider<Meta>(registry);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new InlineRoot(context) { Members = new Dictionary<int, Person> { [1] = new Person { FirstName = "Ann" } } };
        var properties = root.TryGetRegisteredSubject()!.GetAllProperties().ToList();

        // Act
        var results = properties
            .Select(property => (Property: property, Included: provider.TryGetIncludedPropertySegment(property, out var segment), Segment: segment))
            .ToList();

        // Assert
        Assert.Equal(
            ["Members", "first", nameof(Person.LastName)],
            results.Where(result => result.Included).Select(result => result.Segment));
        foreach (var (property, included, segment) in results)
        {
            var expectedIncluded = provider.IsPropertyIncluded(property);
            Assert.Equal(expectedIncluded, included);
            Assert.Equal(expectedIncluded ? provider.TryGetPropertySegment(property) ?? property.BrowseName : null, segment);
        }
    }

    [Fact]
    public void WhenSeparatorConfigured_ThenExposed()
    {
        // Arrange
        var provider = new FluentPathProvider<Meta>(new FluentMappingRegistry<Meta>(), '/');

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
