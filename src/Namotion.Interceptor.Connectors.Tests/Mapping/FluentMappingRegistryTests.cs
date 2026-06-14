using Namotion.Interceptor.Connectors.Mapping;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class FluentMappingRegistryTests
{
    private sealed record Meta(string Value);

    private interface IMotor { }
    private class Motor : IMotor { }
    private sealed class ServoMotor : Motor { }

    [Fact]
    public void WhenPropertyRegistered_ThenResolvesSegmentAndMetadata()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddPropertyMetadata(typeof(Motor), "Speed", "speed", new Meta("m"));

        // Act
        var hasSegment = registry.TryGetSegment(typeof(Motor), "Speed", out var segment);
        var hasMeta = registry.TryGetPropertyMetadata(typeof(Motor), "Speed", out var meta);

        // Assert
        Assert.True(hasSegment);
        Assert.Equal("speed", segment);
        Assert.True(hasMeta);
        Assert.Equal(new Meta("m"), meta);
    }

    [Fact]
    public void WhenRegisteredOnBaseType_ThenResolvesForDerivedType()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddPropertyMetadata(typeof(Motor), "Speed", "speed", new Meta("m"));

        // Act
        var found = registry.TryGetSegment(typeof(ServoMotor), "Speed", out var segment);

        // Assert
        Assert.True(found);
        Assert.Equal("speed", segment);
    }

    [Fact]
    public void WhenRegisteredOnInterface_ThenResolvesForImplementer()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddPropertyMetadata(typeof(IMotor), "Speed", "rpm", new Meta("i"));

        // Act
        var found = registry.TryGetSegment(typeof(Motor), "Speed", out var segment);

        // Assert
        Assert.True(found);
        Assert.Equal("rpm", segment);
    }

    [Fact]
    public void WhenBothDerivedAndBaseRegistered_ThenMostDerivedWins()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddPropertyMetadata(typeof(Motor), "Speed", "base", new Meta("base"));
        registry.AddPropertyMetadata(typeof(ServoMotor), "Speed", "derived", new Meta("derived"));

        // Act
        registry.TryGetSegment(typeof(ServoMotor), "Speed", out var segment);

        // Assert
        Assert.Equal("derived", segment);
    }

    [Fact]
    public void WhenNotRegistered_ThenReturnsFalse()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();

        // Act
        var found = registry.TryGetSegment(typeof(Motor), "Speed", out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void WhenSegmentOmitted_ThenIsRegisteredWithNullSegment()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddPropertyMetadata(typeof(Motor), "Speed", segment: null, new Meta("m"));

        // Act
        var found = registry.TryGetSegment(typeof(Motor), "Speed", out var segment);

        // Assert
        Assert.True(found);
        Assert.Null(segment);
    }

    [Fact]
    public void WhenTypeMetadataRegistered_ThenResolvesWithInheritance()
    {
        // Arrange
        var registry = new FluentMappingRegistry<Meta>();
        registry.AddTypeMetadata(typeof(Motor), new Meta("self"));

        // Act
        var found = registry.TryGetTypeMetadata(typeof(ServoMotor), out var meta);

        // Assert
        Assert.True(found);
        Assert.Equal(new Meta("self"), meta);
    }
}
