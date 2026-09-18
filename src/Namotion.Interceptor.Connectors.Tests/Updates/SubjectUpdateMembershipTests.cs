using System.Text.Json;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

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
        var update = CreateUpdate(dictionary: true, count: 0, propertyName: "ReadOnlyChildren");

        // Act
        Apply(target, update, json: true);

        // Assert
        Assert.Empty(children);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void WhenCompleteMembershipIsAppliedToAReceiverWithNullEntries_ThenFinalMembershipContainsOnlyValidChildren(bool dictionary, int count)
    {
        // Arrange
        var target = CreateTarget();
        var retained = new Person { FirstName = "First", LastName = "Retained" };
        target.Children = [null!];
        target.Relationships = count > 0
            ? new() { ["first"] = retained, ["stale"] = null! }
            : new() { ["stale"] = null! };
        var update = CreateUpdate(dictionary, count);
        if (count > 0)
            update.Subjects[update.Root][PropertyName(dictionary)].Items!.RemoveAt(1);
        update.Subjects["first"][nameof(Person.FirstName)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Updated" };

        // Act
        Apply(target, update, json: true);

        // Assert
        var children = GetChildren(target, dictionary);
        Assert.Equal(count, children.Length);
        if (count > 0)
        {
            var child = Assert.Single(children);
            Assert.NotNull(child);
            Assert.Equal("Updated", child.FirstName);
            if (dictionary)
                Assert.Same(retained, child);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenMembershipIsComplete_ThenUnlistedMembersAreRemovedAndRetainedChildrenKeepOmittedProperties(bool dictionary, bool json)
    {
        // Arrange
        var target = CreateTarget();
        var retained = GetChildren(target, dictionary).Take(2).ToArray();
        var update = CreateUpdate(dictionary, 2);
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
        var update = CreateUpdate(dictionary, 0);
        var propertyUpdate = update.Subjects[update.Root][PropertyName(dictionary)];
        propertyUpdate.Items = initiallyNull ? null : [];
        propertyUpdate.Operations = [];

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
    public void WhenCompleteItemsArriveOutOfOrderOnANullReceiver_ThenAllChildrenAreCreated(bool dictionary, bool json)
    {
        // Arrange
        var target = CreateTarget();
        target.Children = null!;
        target.Relationships = null;
        var update = CreateUpdate(dictionary, 2);
        update.Subjects[update.Root][PropertyName(dictionary)].Items!.Reverse();
        update.Subjects["first"][nameof(Person.FirstName)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "First" };

        // Act
        Apply(target, update, json);

        // Assert
        Assert.Collection(GetChildren(target, dictionary),
            child => Assert.Equal("First", child.FirstName),
            child => Assert.Null(child.FirstName));
    }

    [Theory]
    [InlineData(false, "uncounted")]
    [InlineData(true, "uncounted")]
    [InlineData(false, "incomplete")]
    [InlineData(true, "incomplete")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "null-id")]
    [InlineData(true, "null-id")]
    [InlineData(false, "negative-count")]
    [InlineData(true, "negative-count")]
    [InlineData(false, "negative-index")]
    [InlineData(false, "operations")]
    [InlineData(true, "operations")]
    [InlineData(false, "unmarked")]
    [InlineData(true, "unmarked")]
    public void WhenMembershipIsNotComplete_ThenUnlistedMembersArePreserved(bool dictionary, string scenario)
    {
        // Arrange
        var target = CreateTarget();
        var original = GetChildren(target, dictionary);
        var update = CreateUpdate(dictionary, 2);
        var propertyUpdate = update.Subjects[update.Root][PropertyName(dictionary)];
        switch (scenario)
        {
            case "uncounted": propertyUpdate.Count = null; break;
            case "incomplete": propertyUpdate.Count = 3; break;
            case "duplicate": propertyUpdate.Items![1] = new() { Index = dictionary ? "first" : 0, Id = "second" }; break;
            case "null-id": propertyUpdate.Items![1] = new() { Index = dictionary ? "second" : 1 }; break;
            case "negative-count": propertyUpdate.Count = -1; propertyUpdate.Items = null; break;
            case "negative-index": propertyUpdate.Items![1] = new() { Index = -1, Id = "second" }; break;
            case "operations":
                propertyUpdate.Operations = [new() { Action = SubjectCollectionOperationType.Remove, Index = dictionary ? "absent" : 99 }];
                break;
            case "unmarked": propertyUpdate.Mode = SubjectPropertyUpdateMode.Incremental; break;
        }

        // Act
        Apply(target, update, json: true);

        // Assert: two IDs name the first position in the duplicate scenario, and one instance takes the
        // payload of one ID only, so there the first position gets a new instance.
        var namedTwice = scenario == "duplicate" ? 1 : 0;
        Assert.Equal(original.Skip(namedTwice), GetChildren(target, dictionary).Skip(namedTwice));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WhenDictionaryKeysAreConverted_ThenOnlyDistinctConvertedKeysEstablishCompleteMembership(bool alias, bool json)
    {
        // Arrange
        var target = CreateTarget();
        var children = new Dictionary<int, Person> { [1] = new(), [2] = new(), [3] = new() };
        target.TryGetRegisteredSubject()!.AddProperty("IntegerChildren", typeof(Dictionary<int, Person>),
            _ => children, (_, value) => children = (Dictionary<int, Person>)value!);
        var update = CreateUpdate(dictionary: true, count: 2, propertyName: "IntegerChildren");
        update.Subjects[update.Root]["IntegerChildren"].Items = [new() { Index = json ? 1 : "1", Id = "first" }, new() { Index = json ? 2 : alias ? "01" : "2", Id = "second" }];

        // Act
        Apply(target, update, json);

        // Assert
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
        return new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = children,
            Relationships = new() { ["first"] = children[0], ["second"] = children[1], ["stale"] = children[2] }
        };
    }

    private static string PropertyName(bool dictionary) => dictionary ? nameof(Person.Relationships) : nameof(Person.Children);

    private static Person[] GetChildren(Person target, bool dictionary) => dictionary
        ? target.Relationships!.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray()
        : target.Children.ToArray();

    private static SubjectUpdate CreateUpdate(bool dictionary, int count, string? propertyName = null) => new()
    {
        Root = "root",
        Subjects = new()
        {
            ["root"] = new()
            {
                [propertyName ?? PropertyName(dictionary)] = new()
                {
                    Kind = dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection,
                    Mode = SubjectPropertyUpdateMode.Complete,
                    Count = count,
                    Items = count == 0 ? null : [new() { Index = dictionary ? "first" : 0, Id = "first" }, new() { Index = dictionary ? "second" : 1, Id = "second" }]
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
