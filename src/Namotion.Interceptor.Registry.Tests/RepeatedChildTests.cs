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
        GetRegistryHandler(context).ReplaceChildIndices(
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
        GetRegistryHandler(context).ReplaceChildIndices(
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
        handler.ReplaceChildIndices(property, [new SubjectChild(light, 0), new SubjectChild(light, 2)]);

        // Act
        handler.ReplaceChildIndices(property, [new SubjectChild(light, 0)]);

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
        GetRegistryHandler(context).ReplaceChildIndices(
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
    public void WhenListHoldingAChildTwiceSwapsTwoOtherChildren_ThenEveryEntryHasItsPosition()
    {
        // Arrange
        var repeated = new Light();
        var a = new Light();
        var b = new Light();
        var c = new Light();
        var group = new LightGroup(CreateContext()) { Lights = [repeated, a, b, c, repeated] };

        // Act
        group.Lights = [repeated, b, a, c, repeated];

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (repeated, 0), (b, 1), (a, 2), (c, 3), (repeated, 4));
    }

    [Fact]
    public void WhenListHoldingAChildTwiceSwapsTwoOtherChildrenAndLosesTheSecondPosition_ThenEveryEntryHasItsPosition()
    {
        // Arrange
        var repeated = new Light();
        var a = new Light();
        var b = new Light();
        var c = new Light();
        var group = new LightGroup(CreateContext()) { Lights = [repeated, a, b, c, repeated] };

        // Act
        group.Lights = [repeated, b, a, c];

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (repeated, 0), (b, 1), (a, 2), (c, 3));
    }

    [Fact]
    public void WhenListHoldingAChildTwiceLosesTheSecondPositionAtTheEnd_ThenTheChildKeepsItsFirstEntry()
    {
        // Arrange
        var repeated = new Light();
        var a = new Light();
        var group = new LightGroup(CreateContext()) { Lights = [repeated, a, repeated] };

        // Act
        group.Lights = [repeated, a];

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (repeated, 0), (a, 1));
    }

    [Fact]
    public void WhenListHoldingAChildTwiceSwapsTwoOtherChildrenAndGainsAThirdPosition_ThenEveryEntryHasItsPosition()
    {
        // Arrange
        var repeated = new Light();
        var a = new Light();
        var b = new Light();
        var c = new Light();
        var group = new LightGroup(CreateContext()) { Lights = [repeated, a, b, c, repeated] };

        // Act
        group.Lights = [repeated, b, a, repeated, c, repeated];

        // Assert
        AssertChildrenAndParents(
            group, nameof(LightGroup.Lights), (repeated, 0), (b, 1), (a, 2), (repeated, 3), (c, 4), (repeated, 5));
    }

    [Fact]
    public void WhenDictionaryHoldingAChildTwiceReKeysAnotherChild_ThenEveryEntryHasItsKey()
    {
        // Arrange
        var repeated = new Light();
        var other = new Light();
        var group = new LightGroup(CreateContext())
        {
            LightsByName = new Dictionary<string, Light> { ["x"] = repeated, ["a"] = other, ["y"] = repeated }
        };

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["x"] = repeated, ["b"] = other, ["y"] = repeated };

        // Assert: the keys of a child form one run at the place of its first entry.
        AssertChildrenAndParents(group, nameof(LightGroup.LightsByName), (repeated, "x"), (repeated, "y"), (other, "b"));
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

    [Fact]
    public void WhenSubjectHoldingAChildUnderTwoKeysIsAttached_ThenTheChildHasAnEntryPerKeyInAttachOrder()
    {
        // Arrange
        var light = new Light();
        var group = new LightGroup
        {
            LightsByName = new Dictionary<string, Light> { ["b"] = light, ["a"] = light }
        };
        var room = new Room(CreateContext());

        // Act
        room.Group = group;

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.LightsByName), (light, "b"), (light, "a"));
    }

    [Fact]
    public void WhenSubjectWithTwoPropertiesThatRepeatAChildIsAttached_ThenBothPropertiesHaveAnEntryPerPlace()
    {
        // Arrange
        var listed = new Light();
        var named = new Light();
        var group = new LightGroup
        {
            Lights = [listed, listed],
            LightsByName = new Dictionary<string, Light> { ["x"] = named, ["y"] = named }
        };
        var room = new Room(CreateContext());

        // Act
        room.Group = group;

        // Assert
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (listed, 0), (listed, 1));
        AssertChildrenAndParents(group, nameof(LightGroup.LightsByName), (named, "x"), (named, "y"));
    }

    [Fact]
    public void WhenParentHoldingAChildTwiceIsDetached_ThenTheChildIsUnregisteredAndANewParentHoldingItOnceHasOneEntry()
    {
        // Arrange
        var child = new Person();
        var father = new Person { Children = [child, child] };
        var root = new Person(CreateContext()) { Father = father };

        // Act
        root.Father = null;

        // Assert
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Null(father.TryGetRegisteredSubject());

        // Act
        var newFather = new Person { Children = [child] };
        root.Father = newFather;

        // Assert
        AssertChildrenAndParents(newFather, nameof(Person.Children), (child, 0));
        Assert.Single(child.TryGetRegisteredSubject()!.Parents);
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

    [Fact]
    public void WhenNoChildIsRepeated_ThenPropertyHasNoRepeatedChildren()
    {
        // Arrange
        var group = new LightGroup(CreateContext()) { Lights = [new Light(), new Light()] };

        // Act
        var property = group.TryGetRegisteredSubject()!.TryGetProperty(nameof(LightGroup.Lights))!;

        // Assert
        Assert.False(property.HasRepeatedChildren);
    }

    [Fact]
    public void WhenAChildIsRepeated_ThenPropertyHasRepeatedChildren()
    {
        // Arrange
        var light = new Light();
        var group = new LightGroup(CreateContext()) { Lights = [light, new Light()] };
        var property = group.TryGetRegisteredSubject()!.TryGetProperty(nameof(LightGroup.Lights))!;

        // Act
        group.Lights = [light, group.Lights[1], light];

        // Assert
        Assert.True(property.HasRepeatedChildren);
    }

    [Fact]
    public void WhenTheRepeatGoes_ThenPropertyHasNoRepeatedChildrenAgain()
    {
        // Arrange
        var light = new Light();
        var other = new Light();
        var group = new LightGroup(CreateContext()) { Lights = [light, other, light] };
        var property = group.TryGetRegisteredSubject()!.TryGetProperty(nameof(LightGroup.Lights))!;
        Assert.True(property.HasRepeatedChildren);

        // Act
        group.Lights = [other, light];

        // Assert
        Assert.False(property.HasRepeatedChildren);
    }

    [Fact]
    public void WhenTheRepeatedChildLeaves_ThenPropertyHasNoRepeatedChildrenAgain()
    {
        // Arrange
        var light = new Light();
        var other = new Light();
        var group = new LightGroup(CreateContext()) { Lights = [light, other, light] };
        var property = group.TryGetRegisteredSubject()!.TryGetProperty(nameof(LightGroup.Lights))!;

        // Act
        group.Lights = [other];

        // Assert
        Assert.False(property.HasRepeatedChildren);
    }

    [Fact]
    public void WhenSubjectHasNoParent_ThenItCountsNoProperty()
    {
        // Arrange
        var group = new LightGroup(CreateContext());

        // Act
        var count = group.TryGetRegisteredSubject()!.Parents.CountProperties();

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void WhenOnePropertyHoldsSubjectAtSeveralPlaces_ThenItCountsOneProperty()
    {
        // Arrange
        var light = new Light();
        _ = new LightGroup(CreateContext()) { Lights = [light, new Light(), light] };

        // Act
        var parents = light.TryGetRegisteredSubject()!.Parents;

        // Assert
        Assert.Equal(2, parents.Length);
        Assert.Equal(1, parents.CountProperties());
    }

    [Fact]
    public void WhenSeveralPropertiesHoldSubject_ThenItCountsEachPropertyOnce()
    {
        // Arrange
        var light = new Light();
        _ = new LightGroup(CreateContext())
        {
            Lights = [light, light],
            LightsByName = new Dictionary<string, Light> { ["a"] = light }
        };

        // Act
        var parents = light.TryGetRegisteredSubject()!.Parents;

        // Assert
        Assert.Equal(3, parents.Length);
        Assert.Equal(2, parents.CountProperties());
    }
}
