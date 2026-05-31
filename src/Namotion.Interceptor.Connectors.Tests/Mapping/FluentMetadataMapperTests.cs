using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class FluentMetadataMapperTests
{
    private sealed record Meta(string Value) : IPropertyMapping<Meta>
    {
        public static Meta Merge(Meta primary, Meta fallback) => primary;
    }

    [Fact]
    public void WhenPropertyRegistered_ThenReturnsMetadata()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddType(typeof(MetadataMapperTestSensor), "Temperature", null, new Meta("hot"));
        var mapper = new FluentMetadataMapper<Meta, string>(registry);

        var subject = new MetadataMapperTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out var mapping);

        // Assert
        Assert.True(found);
        Assert.Equal(new Meta("hot"), mapping);
    }

    [Fact]
    public void WhenPropertyNotRegistered_ThenReturnsFalse()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        var mapper = new FluentMetadataMapper<Meta, string>(registry);

        var subject = new MetadataMapperTestSensor(new InterceptorSubjectContext());
        var registered = new RegisteredSubject(subject);
        var property = registered.TryGetProperty("Temperature")!;

        // Act
        var found = mapper.TryGetMapping(property, subject, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public async Task WhenReverseLookup_ThenReturnsNull()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        var mapper = new FluentMetadataMapper<Meta, string>(registry);

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new MetadataMapperTestSensor(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var found = await mapper.TryGetPropertyAsync("anything", registered, CancellationToken.None);

        // Assert - reverse lookup is owned by the path-provider mapper.
        Assert.Null(found);
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class MetadataMapperTestSensor
{
    public partial double Temperature { get; set; }

    public MetadataMapperTestSensor()
    {
        Temperature = 0;
    }
}
