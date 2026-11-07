using HomeBlaze.AI.Mcp;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Storage;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class RulePathProviderTests
{
    [Theory]
    [InlineData("Temperature", "Temperature@HomeBlaze:Metadata")]
    [InlineData(null, "HomeBlaze:Metadata")]
    public void WhenAMetadataMemberHasNoValue_ThenItIsNamedAsWithAValue(string? propertyName, string expectedSegment)
    {
        // Arrange
        var thing = new TestThing(CreateContext()) { Name = "Pump" };
        var member = FluentStorageContainer.GetOrAddMetadataMember(thing.TryGetRegisteredSubject()!, propertyName)!;

        // Act
        var isListed = new StateAttributePathProvider().TryGetPropertySegment(member, out _);
        var isNamed = new RulePathProvider().TryGetPropertySegment(member, out var segment);

        // Assert
        Assert.Null(member.GetValue());
        Assert.False(isListed);
        Assert.True(isNamed);
        Assert.Equal(expectedSegment, segment);
    }

    [Fact]
    public void WhenAnAttributeIsNoState_ThenItIsNamedWithItsPropertyAndNotLikeASiblingProperty()
    {
        // Arrange
        var thing = new TestThing(CreateContext()) { Name = "Pump" };
        var temperature = thing.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestThing.Temperature))!;
        var unit = temperature.AddAttribute("Name", typeof(string), _ => "Celsius", null);
        var provider = new RulePathProvider();

        // Act
        var path = unit.TryGetPath(provider, thing);
        var resolved = provider.TryGetPropertyFromPath(thing.TryGetRegisteredSubject()!, "Name");

        // Assert
        Assert.Equal("Temperature@Name", path);
        Assert.Equal(nameof(TestThing.Name), resolved?.Property.Name);
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }
}
