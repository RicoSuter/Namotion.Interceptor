using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// A property that holds a subject at several keys or positions has a child entry, and the subject a parent entry,
/// for each of them.
/// </summary>
public class RepeatedChildTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static IChildIndexHandler GetRegistryHandler(IInterceptorSubjectContext context)
        => (IChildIndexHandler)context.GetService<ISubjectRegistry>();

    private static void AssertChildrenAndParents(
        IInterceptorSubject holder, string propertyName, params (IInterceptorSubject Subject, object Index)[] expected)
    {
        var property = holder.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        Assert.Equal(expected, property.Children.Select(child => (child.Subject, child.Index!)));

        foreach (var subject in expected.Select(entry => entry.Subject).Distinct())
        {
            Assert.Equal(
                expected.Where(entry => entry.Subject == subject).Select(entry => entry.Index),
                subject.TryGetRegisteredSubject()!.Parents.Where(parent => parent.Property == property).Select(parent => parent.Index!));
        }
    }

    [Fact]
    public void WhenHandlerGetsSeveralPositionsOfAChild_ThenItHasAnEntryPerPositionInPositionOrder()
    {
        // Arrange
        var context = CreateContext();
        var light = new Light();
        var other = new Light();
        var group = new LightGroup(context) { Lights = [light, other] };

        // Act
        GetRegistryHandler(context).HandleChildIndexChanges(
            new PropertyReference(group, nameof(LightGroup.Lights)),
            [new SubjectChild(light, 0), new SubjectChild(light, 2)]);

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (light, 0), (other, 1), (light, 2));
    }

    [Fact]
    public void WhenHandlerGetsSeveralKeysOfAChild_ThenTheyTakeThePlaceOfItsEntry()
    {
        // Arrange
        var context = CreateContext();
        var light = new Light();
        var other = new Light();
        var group = new LightGroup(context)
        {
            LightsByName = new Dictionary<string, Light> { ["a"] = light, ["c"] = other }
        };

        // Act
        GetRegistryHandler(context).HandleChildIndexChanges(
            new PropertyReference(group, nameof(LightGroup.LightsByName)),
            [new SubjectChild(light, "a"), new SubjectChild(light, "b")]);

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.LightsByName), (light, "a"), (light, "b"), (other, "c"));
    }

    [Fact]
    public void WhenHandlerGetsOnePositionOfAChildWithSeveral_ThenItKeepsOneEntry()
    {
        // Arrange
        var context = CreateContext();
        var light = new Light();
        var other = new Light();
        var group = new LightGroup(context) { Lights = [light, other] };
        var property = new PropertyReference(group, nameof(LightGroup.Lights));
        var handler = GetRegistryHandler(context);
        handler.HandleChildIndexChanges(property, [new SubjectChild(light, 0), new SubjectChild(light, 2)]);

        // Act
        handler.HandleChildIndexChanges(property, [new SubjectChild(light, 0)]);

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (light, 0), (other, 1));
    }

    [Fact]
    public void WhenChildWithSeveralEntriesLeavesTheProperty_ThenAllItsEntriesAreRemoved()
    {
        // Arrange: the father keeps the child registered after it leaves the collection.
        var context = CreateContext();
        var child = new Person();
        var other = new Person();
        var root = new Person(context) { Father = child, Children = [child, other] };
        GetRegistryHandler(context).HandleChildIndexChanges(
            new PropertyReference(root, nameof(Person.Children)),
            [new SubjectChild(child, 0), new SubjectChild(child, 2)]);

        // Act
        root.Children = [other];

        // Assert
        AssertChildrenAndParents(root, nameof(Person.Children), (other, 0));
        var parent = Assert.Single(child.TryGetRegisteredSubject()!.Parents);
        Assert.Equal(nameof(Person.Father), parent.Property.Name);
    }
}
