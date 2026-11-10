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

    [Fact]
    public void WhenListHoldsAChildAtTwoPositions_ThenItHasAnEntryPerPosition()
    {
        // Arrange
        var light = new Light();
        var other = new Light();

        // Act
        var group = new LightGroup(CreateContext()) { Lights = [light, other, light] };

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (light, 0), (other, 1), (light, 2));
    }

    [Fact]
    public void WhenDictionaryHoldsAChildUnderTwoKeys_ThenItHasAnEntryPerKey()
    {
        // Arrange
        var light = new Light();

        // Act
        var group = new LightGroup(CreateContext())
        {
            LightsByName = new Dictionary<string, Light> { ["a"] = light, ["b"] = light }
        };

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.LightsByName), (light, "a"), (light, "b"));
    }

    [Fact]
    public void WhenListLosesOneOfTwoPositionsOfAChild_ThenTheOtherEntryStays()
    {
        // Arrange
        var light = new Light();
        var other = new Light();
        var group = new LightGroup(CreateContext()) { Lights = [light, other, light] };

        // Act
        group.Lights = [other, light];

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (other, 0), (light, 1));
    }

    [Fact]
    public void WhenListIsWrittenRepeatedly_ThenChildrenAndParentsMatchTheValueAfterEachWrite()
    {
        // Arrange
        var a = new Light();
        var b = new Light();
        var group = new LightGroup(CreateContext());
        List<Light>[] values = [[a, b, a], [b, a, a, b], [a], [], [b, b, b], [a, b], [b, a, b, a]];

        foreach (var value in values)
        {
            // Act
            group.Lights = value;

            // Assert
            AssertChildrenAndParents(
                group, nameof(LightGroup.Lights),
                value.Select((light, index) => ((IInterceptorSubject)light, (object)index)).ToArray());
        }
    }

    [Fact]
    public void WhenParentHoldingAChildTwiceIsAttached_ThenTheChildHasAnEntryPerPosition()
    {
        // Arrange
        var child = new Person();
        var father = new Person { Children = [child, child] };
        var root = new Person(CreateContext());

        // Act
        root.Father = father;

        // Assert
        AssertChildrenAndParents(father, nameof(Person.Children), (child, 0), (child, 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenPlacesOfAChildChange_ThenItsParentEntryOfAnotherPropertyKeepsItsPosition(bool fatherIsFirst)
    {
        // Arrange
        var child = new Person();
        var other = new Person();
        var root = new Person(CreateContext());
        if (fatherIsFirst)
        {
            root.Father = child;
            root.Children = [child, child];
        }
        else
        {
            root.Children = [child, child];
            root.Father = child;
        }

        Person[][] values = [[other, child, child], [child, other, child, child], [child], [child, child]];
        foreach (var value in values)
        {
            // Act
            root.Children = value;

            // Assert
            var childrenEntries = value
                .Select((person, index) => (Person: person, Index: index))
                .Where(entry => entry.Person == child)
                .Select(entry => (nameof(Person.Children), (object?)entry.Index));
            (string, object?)[] fatherEntry = [(nameof(Person.Father), null)];
            Assert.Equal(
                fatherIsFirst ? fatherEntry.Concat(childrenEntries) : childrenEntries.Concat(fatherEntry),
                child.TryGetRegisteredSubject()!.Parents.Select(parent => (parent.Property.Name, parent.Index)));
        }
    }
}
