using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class StructureVersionTests
{
    [Fact]
    public void WhenASubjectIsAttached_ThenStructureVersionIncreases()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        var before = registry.StructureVersion;

        // Act
        root.Father = new Person();

        // Assert
        Assert.True(registry.StructureVersion > before);
    }

    [Fact]
    public void WhenASubjectIsDetached_ThenStructureVersionIncreases()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context) { Father = new Person() };
        var before = registry.StructureVersion;

        // Act
        root.Father = null;

        // Assert
        Assert.True(registry.StructureVersion > before);
    }

    [Fact]
    public void WhenASubjectGainsASecondParent_ThenStructureVersionIncreases()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var child = new Person();
        var root = new Person(context) { Father = child };
        var before = registry.StructureVersion;

        // Act
        root.Mother = child;

        // Assert
        Assert.True(registry.StructureVersion > before);
    }

    [Fact]
    public void WhenASubjectLosesOneOfTwoParents_ThenStructureVersionIncreases()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var child = new Person();
        var root = new Person(context) { Father = child, Mother = child };
        var before = registry.StructureVersion;

        // Act
        root.Mother = null;

        // Assert
        Assert.NotNull(child.TryGetRegisteredSubject());
        Assert.True(registry.StructureVersion > before);
    }

    [Fact]
    public void WhenACollectionIsReordered_ThenStructureVersionIncreases()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var first = new Person();
        var second = new Person();
        var root = new Person(context) { Children = [first, second] };
        var before = registry.StructureVersion;

        // Act
        root.Children = [second, first];

        // Assert
        Assert.True(registry.StructureVersion > before);
    }

    [Fact]
    public void WhenADictionaryEntryIsReKeyed_ThenStructureVersionIncreases()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var light = new Light();
        var group = new LightGroup(context) { LightsByName = new Dictionary<string, Light> { ["a"] = light } };
        var before = registry.StructureVersion;

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["b"] = light };

        // Assert
        Assert.True(registry.StructureVersion > before);
    }

    [Fact]
    public void WhenAPropertyIsAddedAtRunTime_ThenStructureVersionIncreases()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        var before = registry.StructureVersion;

        // Act
        root.TryGetRegisteredSubject()!.AddProperty<string>("Dynamic", _ => "value");

        // Assert
        Assert.True(registry.StructureVersion > before);
    }

    [Fact]
    public void WhenAnAttributeIsAddedAtRunTime_ThenStructureVersionIncreases()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        var before = registry.StructureVersion;

        // Act
        root.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.LastName))!.AddAttribute<string>("Hint", _ => "value");

        // Assert
        Assert.True(registry.StructureVersion > before);
    }

    [Fact]
    public void WhenAScalarPropertyIsWritten_ThenStructureVersionIsUnchanged()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        var before = registry.StructureVersion;

        // Act
        root.FirstName = "Ann";

        // Assert
        Assert.Equal(before, registry.StructureVersion);
    }

    private static (IInterceptorSubjectContext Context, ISubjectRegistry Registry) CreateContext()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return (context, context.TryGetService<ISubjectRegistry>()!);
    }
}
