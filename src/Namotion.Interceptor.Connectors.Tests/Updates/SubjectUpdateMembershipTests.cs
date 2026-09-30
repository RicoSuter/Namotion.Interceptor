using System.Text.Json;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies that the items of a collection or dictionary update state its whole membership on the receiver:
/// listed subjects are resolved by ID or created, and members not listed are removed.
/// </summary>
public class SubjectUpdateMembershipTests
{
    [Fact]
    public void WhenEmptyMembershipReplacesAGenericOnlyDictionaryWithANullEntry_ThenTheEntryIsRemoved()
    {
        // Arrange
        var target = CreateTarget();
        IReadOnlyDictionary<string, Person> children = new GenericOnlyDictionary(new() { ["stale"] = null! });
        target.TryGetRegisteredSubject()!.AddProperty("ReadOnlyChildren", typeof(IReadOnlyDictionary<string, Person>),
            _ => children, (_, value) => children = (IReadOnlyDictionary<string, Person>)value!);
        var update = CreateUpdate(dictionary: true, [], propertyName: "ReadOnlyChildren");

        // Act
        Apply(target, update, json: true);

        // Assert
        Assert.Empty(children);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void WhenMembershipIsAppliedToAReceiverWithNullEntries_ThenFinalMembershipContainsOnlyListedChildren(bool dictionary, int count)
    {
        // Arrange
        var target = CreateTarget();
        target.Children = [null!];
        target.Relationships = new() { ["stale"] = null! };
        var update = CreateUpdate(dictionary, count > 0 ? ["first"] : []);
        update.Subjects["first"][nameof(Person.FirstName)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Updated" };

        // Act
        Apply(target, update, json: true);

        // Assert
        var children = GetChildren(target, dictionary);
        Assert.Equal(count, children.Length);
        if (count > 0)
        {
            Assert.Equal("Updated", Assert.Single(children).FirstName);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenMembershipIsApplied_ThenUnlistedMembersAreRemovedAndRetainedChildrenKeepOmittedProperties(bool dictionary, bool json)
    {
        // Arrange
        var target = CreateTarget();
        var retained = GetChildren(target, dictionary).Take(2).ToArray();
        var update = CreateUpdate(dictionary, ["first", "second"]);
        update.Subjects["first"][nameof(Person.FirstName)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Updated" };

        // Act
        Apply(target, update, json);

        // Assert
        Assert.Collection(GetChildren(target, dictionary),
            child => Assert.Same(retained[0], child),
            child => Assert.Same(retained[1], child));
        Assert.Equal("Updated", retained[0].FirstName);
        Assert.Equal("Second", retained[1].FirstName);
        Assert.All(retained, child => Assert.Equal("Retained", child.LastName));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenMembershipIsEmpty_ThenTheReceiverHasAnEmptyContainer(bool dictionary, bool initiallyNull)
    {
        // Arrange
        var target = CreateTarget();
        if (initiallyNull)
        {
            target.Children = null!;
            target.Relationships = null;
        }
        var update = CreateUpdate(dictionary, []);

        // Act
        Apply(target, update, json: true);

        // Assert
        Assert.NotNull(target.TryGetRegisteredProperty(PropertyName(dictionary))!.GetValue());
        Assert.Empty(GetChildren(target, dictionary));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenMembershipArrivesOnANullContainer_ThenEveryMemberIsCreatedInItemsOrder(bool dictionary, bool json)
    {
        // Arrange
        var target = CreateTarget();
        target.Children = null!;
        target.Relationships = null;
        var update = CreateUpdate(dictionary, ["second", "first"]);
        update.Subjects["first"][nameof(Person.FirstName)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "First" };

        // Act
        Apply(target, update, json);

        // Assert
        Assert.Collection(dictionary ? [target.Relationships!["first"], target.Relationships["second"]] : target.Children,
            child => Assert.Equal(dictionary ? "First" : null, child.FirstName),
            child => Assert.Equal(dictionary ? null : "First", child.FirstName));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WhenDictionaryKeysAreConverted_ThenTwoKeysConvertingToOneFailTheProperty(bool alias, bool json)
    {
        // Arrange
        var target = CreateTarget();
        var children = new Dictionary<int, Person> { [1] = new(), [2] = new(), [3] = new() };
        target.TryGetRegisteredSubject()!.AddProperty("IntegerChildren", typeof(Dictionary<int, Person>),
            _ => children, (_, value) => children = (Dictionary<int, Person>)value!);
        var update = CreateUpdate(dictionary: true, [], propertyName: "IntegerChildren");
        update.Subjects[update.Root!]["IntegerChildren"].Items =
        [
            new() { Key = "1", Id = "first" },
            new() { Key = alias ? "01" : "2", Id = "second" }
        ];

        // Act
        var exception = Record.Exception(() => Apply(target, update, json));

        // Assert
        Assert.Equal(alias, exception is InvalidOperationException);
        Assert.Equal(alias ? new[] { 1, 2, 3 } : new[] { 1, 2 }, children.Keys.Order());
    }

    [Fact]
    public void WhenASubjectIsReattachedWhileItsCollectionShrinks_ThenTheMirrorKeepsOnlyTheRemainingChild()
    {
        // Arrange
        var remainingChild = new Person { FirstName = "Kept" };
        var removedChild = new Person { FirstName = "Gone" };
        var mother = new Person { FirstName = "Mom", Children = [remainingChild, removedChild] };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Mother = mother };
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // One batch shrinks the collection, then detaches and re-attaches its owner.
        var previousChildren = mother.Children;
        mother.Children = [remainingChild];
        source.Mother = null;
        source.Mother = mother;
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create(new PropertyReference(mother, nameof(Person.Children)),
                ChangeOrigin.Local, timestamp, null, previousChildren, mother.Children),
            SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Mother)),
                ChangeOrigin.Local, timestamp, null, mother, null),
            SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Mother)),
                ChangeOrigin.Local, timestamp, null, null, mother)
        ];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        mirror.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Mom", mirror.Mother!.FirstName);
        Assert.Equal(["Kept"], mirror.Mother.Children.Select(child => child.FirstName));
    }

    private static Person CreateTarget()
    {
        var children = new[] { "First", "Second", "Stale" }
            .Select(name => new Person { FirstName = name, LastName = "Retained" }).ToList();
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = children,
            Relationships = new() { ["first"] = children[0], ["second"] = children[1], ["stale"] = children[2] }
        };

        children[0].SetSubjectId("first");
        children[1].SetSubjectId("second");
        children[2].SetSubjectId("stale");
        return target;
    }

    private static string PropertyName(bool dictionary) => dictionary ? nameof(Person.Relationships) : nameof(Person.Children);

    private static Person[] GetChildren(Person target, bool dictionary) => dictionary
        ? target.Relationships!.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray()
        : target.Children.ToArray();

    private static SubjectUpdate CreateUpdate(bool dictionary, string[] ids, string? propertyName = null) => new()
    {
        Root = "root",
        Subjects = new()
        {
            ["root"] = new()
            {
                [propertyName ?? PropertyName(dictionary)] = new()
                {
                    Kind = dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection,
                    Items = ids.Select(id => new SubjectPropertyItemUpdate { Id = id, Key = dictionary ? id : null }).ToList()
                }
            },
            ["first"] = new(),
            ["second"] = new()
        }
    };

    private static void Apply(Person target, SubjectUpdate update, bool json)
    {
        if (json)
            update = JsonSerializer.Deserialize<SubjectUpdate>(JsonSerializer.Serialize(update))!;
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
    }

    private sealed class GenericOnlyDictionary(Dictionary<string, Person> entries) : IReadOnlyDictionary<string, Person>
    {
        public Person this[string key] => entries[key];
        public IEnumerable<string> Keys => entries.Keys;
        public IEnumerable<Person> Values => entries.Values;
        public int Count => entries.Count;
        public bool ContainsKey(string key) => entries.ContainsKey(key);
        public bool TryGetValue(string key, out Person value) => entries.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, Person>> GetEnumerator() => entries.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
