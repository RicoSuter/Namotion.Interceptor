using System.Text.Json;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectRegistryTests
{
    [Fact]
    public Task WhenTwoChildrenAreAttachedSequentially_ThenWeHaveThreeAttaches()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        // Assert
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(3, registry.KnownSubjects.Count());

        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenRemovingSubjectWithChild_ThenBothDetach()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        person.Mother = null;

        // Assert
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Single(registry.KnownSubjects);

        return Verify(handler.GetEvents());
    }

    [Fact]
    public void WhenAddingTransitiveProxies_ThenAllAreAvailable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act
        var grandmother = new Person
        {
            FirstName = "Grandmother"
        };

        var mother = new Person
        {
            FirstName = "Mother",
            Mother = grandmother
        };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother
        };

        // Assert
        Assert.Equal(3, registry.KnownSubjects.Count);
        Assert.Contains(person, registry.KnownSubjects.Keys);
        Assert.Contains(mother, registry.KnownSubjects.Keys);
        Assert.Contains(grandmother, registry.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenRemovingMiddleElement_ThenChildrenAreAlsoRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act
        var grandmother = new Person
        {
            FirstName = "Grandmother"
        };

        var mother = new Person
        {
            FirstName = "Mother",
            Mother = grandmother
        };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother
        };

        mother.Mother = null;

        // Assert
        Assert.Equal(2, registry.KnownSubjects.Count());
        Assert.Contains(person, registry.KnownSubjects.Keys);
        Assert.Contains(mother, registry.KnownSubjects.Keys);
        Assert.DoesNotContain(grandmother, registry.KnownSubjects.Keys);
    }

    [Fact]
    public async Task WhenConvertingToJson_ThenGraphIsPreserved()
    {
        // TODO: Move to Namotion.Interceptor.AspNetCore.Tests
        
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };
        
        // Assert
        var jsonSerializerOptions = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var property = person.Mother.Mother
            .TryGetRegisteredProperty("FirstName")!;
        
        var path = property.Reference.GetJsonPath(jsonSerializerOptions);
        
        Assert.Equal("mother.mother.firstName", path);
        await Verify(person.ToJsonObject(jsonSerializerOptions).ToJsonString(jsonSerializerOptions));
    }

    [Fact]
    public async Task WhenChangingCollection_ThenIndexAreCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Children = [child1, child2, child3]
        };

        // Act
        person.Children = person.Children.Union([new Person { FirstName = "Child4" }]).ToArray(); // add child4
        person.Children = person.Children.Skip(2).ToArray(); // remove child1 and child2
        
        // Assert
        var children = person
            .TryGetRegisteredSubject()?
            .TryGetProperty(nameof(Person.Children))?
            .Children
            .Select(c => new
            {
                Index = c.Index,
                Subject = c.Subject is Person p ? p.FirstName : "n/a"
            });

        await Verify(children).DisableDateCounting();
    }
    
    [Fact]
    public async Task WhenCreatingSubjectWithInheritance_ThenAllPropertiesAreAvailable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher(context);

        // Act
        var properties = teacher.TryGetRegisteredSubject()!.Properties;
        
        // Assert
        await Verify(properties.Select(p => p.Name));
    }

    [Fact]
    public void WhenRemovingMultipleCollectionItems_ThenNoChildrenAreLost()
    {
        // Regression test for memory leak: forward-order removal in recursive detach
        // caused IndexOf to fail because renumbered indices didn't match the lifecycle event indices.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var children = Enumerable.Range(0, 10)
            .Select(i => new Person { FirstName = $"Child{i}" })
            .ToArray();

        var person = new Person(context) { Children = children };

        // Act
        person.Children = children[8..];

        // Assert
        var registeredChildren = person
            .TryGetRegisteredSubject()?
            .TryGetProperty(nameof(Person.Children))?
            .Children;

        Assert.NotNull(registeredChildren);
        Assert.Equal(2, registeredChildren.Value.Length);
        Assert.Equal("Child8", ((Person)registeredChildren.Value[0].Subject).FirstName);
        Assert.Equal(0, registeredChildren.Value[0].Index);
        Assert.Equal("Child9", ((Person)registeredChildren.Value[1].Subject).FirstName);
        Assert.Equal(1, registeredChildren.Value[1].Index);
    }

    [Fact]
    public void WhenRemovingCollectionItems_ThenParentsAndChildrenIndicesAreConsistent()
    {
        // Regression test: old code renumbered Children indices but never updated Parents,
        // causing path resolution mismatches.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "A" };
        var child2 = new Person { FirstName = "B" };
        var child3 = new Person { FirstName = "C" };

        var person = new Person(context)
        {
            Children = [child1, child2, child3]
        };

        // Act
        person.Children = [child1, child3];

        // Assert
        var childrenProp = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Children))!;

        Assert.Same(child1, childrenProp.Children[0].Subject);
        Assert.Equal(0, childrenProp.Children[0].Index);
        Assert.Same(child3, childrenProp.Children[1].Subject);
        Assert.Equal(1, childrenProp.Children[1].Index);

        var child1Parents = child1.TryGetRegisteredSubject()!.Parents;
        Assert.Single(child1Parents);
        Assert.Equal(0, child1Parents[0].Index); // position 0 in [A, C]

        var child3Parents = child3.TryGetRegisteredSubject()!.Parents;
        Assert.Single(child3Parents);
        Assert.Equal(1, child3Parents[0].Index); // position 1 in [A, C]
    }

    [Fact]
    public void WhenReorderingCollection_ThenIndicesMatchLiveCollection()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "A" };
        var child2 = new Person { FirstName = "B" };
        var child3 = new Person { FirstName = "C" };

        var person = new Person(context)
        {
            Children = [child1, child2, child3]
        };

        // Act
        person.Children = [child3, child2, child1];

        // Assert
        var childrenProp = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Children))!;

        Assert.Equal(3, childrenProp.Children.Length);

        var childBySubject = childrenProp.Children.ToDictionary(c => ((Person)c.Subject).FirstName!, c => c.Index);
        Assert.Equal(0, childBySubject["C"]); // child3 now at position 0
        Assert.Equal(1, childBySubject["B"]); // child2 still at position 1
        Assert.Equal(2, childBySubject["A"]); // child1 now at position 2

        // Parents should also match
        Assert.Equal(2, child1.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(1, child2.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(0, child3.TryGetRegisteredSubject()!.Parents[0].Index);
    }

    [Fact]
    public void WhenMovingDictionaryItemToAnotherKey_ThenKeysAreCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var directory = new PersonDirectory(context)
        {
            PeopleByName = new Dictionary<string, Person> { ["alpha"] = child, ["beta"] = other }
        };

        // Act: child moves to another key, other keeps its own, so neither detaches
        directory.PeopleByName = new Dictionary<string, Person> { ["gamma"] = child, ["beta"] = other };

        // Assert
        var childrenProp = directory.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(PersonDirectory.PeopleByName))!;

        var keyBySubject = childrenProp.Children.ToDictionary(c => ((Person)c.Subject).FirstName!, c => c.Index);
        Assert.Equal("gamma", keyBySubject["A"]);
        Assert.Equal("beta", keyBySubject["B"]);

        Assert.Equal("gamma", child.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal("beta", other.TryGetRegisteredSubject()!.Parents[0].Index);
    }

    [Fact]
    public void WhenMovingDictionaryItemToAnotherKeyAndRemovingIt_ThenNoChildIsLeftBehind()
    {
        // The removal's index comes from the value written before it, so a key left stale by the re-key
        // makes RemoveChild's exact match miss and the detached subject stays in Children forever.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var directory = new PersonDirectory(context)
        {
            PeopleByName = new Dictionary<string, Person> { ["alpha"] = child, ["beta"] = other }
        };

        // Act
        directory.PeopleByName = new Dictionary<string, Person> { ["gamma"] = child, ["beta"] = other };
        directory.PeopleByName = new Dictionary<string, Person> { ["beta"] = other };

        // Assert
        var childrenProp = directory.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(PersonDirectory.PeopleByName))!;

        Assert.Single(childrenProp.Children);
        Assert.Same(other, childrenProp.Children[0].Subject);
        Assert.Null(child.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenMovingDictionaryItemToAnotherKeyInAReadOnlyDictionary_ThenKeysAreCorrect()
    {
        // A read-only dictionary that implements neither IDictionary nor ICollection has to be enumerated
        // as key-value pairs to find its keys.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var directory = new PersonDirectory(context)
        {
            PeopleByName = new ReadOnlyPersonDictionary(new Dictionary<string, Person> { ["alpha"] = child, ["beta"] = other })
        };

        // Act
        directory.PeopleByName = new ReadOnlyPersonDictionary(new Dictionary<string, Person> { ["gamma"] = child, ["beta"] = other });

        // Assert
        Assert.Equal("gamma", child.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal("beta", other.TryGetRegisteredSubject()!.Parents[0].Index);
    }

    [Fact]
    public void WhenMovingItemToAnotherKeyInAnObjectDeclaredProperty_ThenNoChildIsLeftBehind()
    {
        // The declared type says nothing about the shape here, so the keys can only come from the value.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var directory = new PersonDirectory(context)
        {
            Untyped = new Dictionary<string, Person> { ["alpha"] = child, ["beta"] = other }
        };

        // Act
        directory.Untyped = new Dictionary<string, Person> { ["gamma"] = child, ["beta"] = other };
        directory.Untyped = new Dictionary<string, Person> { ["beta"] = other };

        // Assert
        var untypedProp = directory.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(PersonDirectory.Untyped))!;

        Assert.Single(untypedProp.Children);
        Assert.Same(other, untypedProp.Children[0].Subject);
        Assert.Null(child.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenAnObjectDeclaredPropertyGoesFromDictionaryToCollection_ThenTheWriteSucceeds()
    {
        // Children left over from the dictionary still carry string keys, so ordering the collection has to
        // tolerate indices that are not positions.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var directory = new PersonDirectory(context)
        {
            Untyped = new Dictionary<string, Person> { ["alpha"] = child, ["alpha2"] = child, ["beta"] = other }
        };

        directory.Untyped = new Dictionary<string, Person> { ["beta"] = other };

        // Act
        var exception = Record.Exception(() => directory.Untyped = new List<Person> { other });

        // Assert
        Assert.Null(exception);

        var untypedProp = directory.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(PersonDirectory.Untyped))!;

        Assert.Contains(untypedProp.Children, c => ReferenceEquals(c.Subject, other) && Equals(c.Index, 0));
    }

    [Fact]
    public void WhenAnObjectDeclaredPropertyGoesFromCollectionToTheSubjectItself_ThenTheIndexIsDropped()
    {
        // Nothing attaches or detaches here, the subject is simply held directly instead of at a position,
        // so the refresh is the only thing that can clear its index.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };

        var directory = new PersonDirectory(context)
        {
            Untyped = new List<Person> { child }
        };

        // Act
        directory.Untyped = child;

        // Assert
        Assert.Null(child.TryGetRegisteredSubject()!.Parents[0].Index);

        var untypedProp = directory.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(PersonDirectory.Untyped))!;

        Assert.Null(untypedProp.Children.Single().Index);
    }

    [Fact]
    public void WhenAnObjectDeclaredPropertyGoesFromCollectionToTheSubjectAndThenAway_ThenNoChildIsLeftBehind()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };

        var directory = new PersonDirectory(context)
        {
            Untyped = new List<Person> { child }
        };

        // Act
        directory.Untyped = child;
        directory.Untyped = null;

        // Assert
        var untypedProp = directory.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(PersonDirectory.Untyped))!;

        Assert.Empty(untypedProp.Children);
        Assert.Null(child.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenASubjectHeldTwiceInOneCollectionIsRemoved_ThenNoParentEntryIsLeftBehind()
    {
        // Attach records one entry however many times the subject appears, so removal has to drop that one
        // entry whichever of the two indices the detach happens to carry.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var keeper = new Person { FirstName = "K" };
        var shared = new Person { FirstName = "X" };
        _ = new Person(context) { FirstName = "Keeper", Children = [keeper] };

        var person = new Person(context) { Children = [shared, shared] };

        // Act
        person.Children = [];

        // Assert
        var childrenProp = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Children))!;

        Assert.Empty(childrenProp.Children);
        Assert.Empty(shared.GetParents());
        Assert.Null(shared.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenASubjectHeldUnderTwoKeysIsRemoved_ThenNoParentEntryIsLeftBehind()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var shared = new Person { FirstName = "X" };

        var directory = new PersonDirectory(context)
        {
            PeopleByName = new Dictionary<string, Person> { ["alpha"] = shared, ["beta"] = shared }
        };

        // Act
        directory.PeopleByName = new Dictionary<string, Person>();

        // Assert
        var peopleProp = directory.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(PersonDirectory.PeopleByName))!;

        Assert.Empty(peopleProp.Children);
        Assert.Empty(shared.GetParents());
        Assert.Null(shared.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenACollectionIsReordered_ThenBothParentIndexCopiesAgree()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        var third = new Person { FirstName = "C" };

        var person = new Person(context) { Children = [first, second, third] };

        // Act
        person.Children = [third, second, first];

        // Assert
        Assert.Equal(2, first.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(2, first.GetParents().Single().Index);
        Assert.Equal(0, third.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(0, third.GetParents().Single().Index);
    }

    [Fact]
    public void WhenAStrandedChildKeepsAKeyAndTheValueBecomesACollection_ThenOrderingDoesNotThrow()
    {
        // The stranded child still carries a key, so ordering the collection meets a mix of keys and
        // positions. In-place mutation is unsupported, but it must not turn a write into an exception.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var stranded = new Person { FirstName = "A" };
        var kept = new Person { FirstName = "B" };

        var map = new Dictionary<string, Person> { ["alpha"] = stranded, ["beta"] = kept };
        var directory = new PersonDirectory(context) { Untyped = map };

        map.Remove("alpha");

        // Act
        var exception = Record.Exception(() => directory.Untyped = new List<Person> { kept });

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void WhenARetainedItemMoves_ThenTheTrackedParentsAgreeWithTheRegistry()
    {
        // Two copies of the same index: RegisteredSubject.Parents and the tracked parents behind
        // GetParents, which the JSON path helpers read. They have to move together.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var directory = new PersonDirectory(context)
        {
            PeopleByName = new Dictionary<string, Person> { ["alpha"] = child, ["beta"] = other }
        };

        // Act
        directory.PeopleByName = new Dictionary<string, Person> { ["gamma"] = child, ["beta"] = other };

        // Assert
        Assert.Equal("gamma", child.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal("gamma", child.GetParents().Single().Index);
    }

    [Fact]
    public void WhenACollectionItemIsReorderedAndThenRemoved_ThenItKeepsNoTrackedParent()
    {
        // The reorder moves the index; if the tracked copy is left behind, the removal cannot match it and
        // the detached child keeps a parent entry that pins its former parent alive.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };

        var person = new Person(context) { Children = [first, second] };

        // Act
        person.Children = [second, first];
        person.Children = [second];

        // Assert
        Assert.Null(first.TryGetRegisteredSubject());
        Assert.Empty(first.GetParents());
    }

    [Fact]
    public void WhenAStoredIndexNoLongerMatches_ThenRemovalStillFindsTheChild()
    {
        // In-place mutation is unsupported and reports nothing, so it is the way to leave a stored index
        // behind on purpose. Even then removal has to find the child, or it is stranded for good.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var map = new Dictionary<string, Person> { ["alpha"] = child, ["beta"] = other };
        var directory = new PersonDirectory(context) { PeopleByName = map };

        // Act: the stored key is now stale, and the next write reports "delta" for the same child
        map.Remove("alpha");
        map["delta"] = child;
        directory.PeopleByName = new Dictionary<string, Person> { ["beta"] = other };

        // Assert
        var peopleProp = directory.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(PersonDirectory.PeopleByName))!;

        Assert.Single(peopleProp.Children);
        Assert.Same(other, peopleProp.Children[0].Subject);
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Empty(child.GetParents());
    }

    [Fact]
    public void WhenReorderingAndRekeyingDictionaryInOneWrite_ThenKeysFollowTheItems()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var directory = new PersonDirectory(context)
        {
            PeopleByName = new Dictionary<string, Person> { ["alpha"] = child, ["beta"] = other }
        };

        // Act: entries swap position and one of them swaps key
        directory.PeopleByName = new Dictionary<string, Person> { ["beta"] = other, ["gamma"] = child };

        // Assert
        Assert.Equal("gamma", child.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal("beta", other.TryGetRegisteredSubject()!.Parents[0].Index);
    }

    [Fact]
    public void WhenReorderingDictionary_ThenKeysAreUnchanged()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "A" };
        var other = new Person { FirstName = "B" };

        var directory = new PersonDirectory(context)
        {
            PeopleByName = new Dictionary<string, Person> { ["alpha"] = child, ["beta"] = other }
        };

        // Act
        directory.PeopleByName = new Dictionary<string, Person> { ["beta"] = other, ["alpha"] = child };

        // Assert
        Assert.Equal("alpha", child.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal("beta", other.TryGetRegisteredSubject()!.Parents[0].Index);
    }

    [Fact]
    public void WhenInsertingInMiddleOfCollection_ThenIndicesAreCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "A" };
        var child3 = new Person { FirstName = "C" };

        var person = new Person(context)
        {
            Children = [child1, child3]
        };

        // Act
        var child2 = new Person { FirstName = "B" };
        person.Children = [child1, child2, child3];

        // Assert
        var childrenProp = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Children))!;

        Assert.Equal(3, childrenProp.Children.Length);

        var childBySubject = childrenProp.Children.ToDictionary(c => ((Person)c.Subject).FirstName!, c => c.Index);
        Assert.Equal(0, childBySubject["A"]);
        Assert.Equal(1, childBySubject["B"]);
        Assert.Equal(2, childBySubject["C"]);

        // All parent indices should match live positions
        Assert.Equal(0, child1.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(1, child2.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Equal(2, child3.TryGetRegisteredSubject()!.Parents[0].Index); // updated from 1 to 2
    }
}