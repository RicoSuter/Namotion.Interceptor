using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectRegistryCycleTests
{
    [Fact]
    public void WhenCreatingSelfReference_ThenSubjectIsRegisteredOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act - Create self-reference using Father property
        var person = new Person(context)
        {
            FirstName = "Narcissus",
            Father = null // Will set to self
        };
        person.Father = person; // Self-reference

        // Assert
        Assert.Single(registry.KnownSubjects);

        var registered = registry.TryGetRegisteredSubject(person);
        Assert.NotNull(registered);

        // Verify the Father property has the person as a child
        var fatherProperty = registered.TryGetProperty(nameof(Person.Father));
        Assert.NotNull(fatherProperty);
        Assert.Single(fatherProperty.Children);
        Assert.Equal(person, fatherProperty.Children.First().Subject);

        // Verify the person has itself as a parent
        Assert.Contains(registered.Parents, p => p.Property.Name == nameof(Person.Father));
    }

    [Fact]
    public void WhenCreatingDirectCycle_ThenBothSubjectsAreRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act - Create bidirectional cycle: person1.Mother = person2, person2.Mother = person1
        var person1 = new Person(context)
        {
            FirstName = "Alice"
        };

        var person2 = new Person(context)
        {
            FirstName = "Bob",
            Mother = person1
        };

        person1.Mother = person2; // Complete the cycle

        // Assert
        Assert.Equal(2, registry.KnownSubjects.Count);

        var registered1 = registry.TryGetRegisteredSubject(person1)!;
        var registered2 = registry.TryGetRegisteredSubject(person2)!;

        // Verify person1.Mother points to person2
        var person1Mother = registered1.TryGetProperty(nameof(Person.Mother))!;
        Assert.Single(person1Mother.Children);
        Assert.Equal(person2, person1Mother.Children.First().Subject);

        // Verify person2.Mother points to person1
        var person2Mother = registered2.TryGetProperty(nameof(Person.Mother))!;
        Assert.Single(person2Mother.Children);
        Assert.Equal(person1, person2Mother.Children.First().Subject);

        // Verify both have each other as parents
        Assert.Contains(registered1.Parents, p => p.Property.Parent.Subject == person2);
        Assert.Contains(registered2.Parents, p => p.Property.Parent.Subject == person1);
    }

    [Fact]
    public void WhenCreatingLongerCycle_ThenAllSubjectsAreRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act - Create cycle: A → B → C → A
        var personA = new Person(context)
        {
            FirstName = "A"
        };

        var personB = new Person(context)
        {
            FirstName = "B"
        };

        var personC = new Person(context)
        {
            FirstName = "C"
        };

        personA.Mother = personB;
        personB.Mother = personC;
        personC.Mother = personA; // Complete the cycle

        // Assert
        Assert.Equal(3, registry.KnownSubjects.Count);

        var registeredA = registry.TryGetRegisteredSubject(personA)!;
        var registeredB = registry.TryGetRegisteredSubject(personB)!;
        var registeredC = registry.TryGetRegisteredSubject(personC)!;

        // Verify the cycle
        Assert.Equal(personB, registeredA.TryGetProperty(nameof(Person.Mother))!.Children.First().Subject);
        Assert.Equal(personC, registeredB.TryGetProperty(nameof(Person.Mother))!.Children.First().Subject);
        Assert.Equal(personA, registeredC.TryGetProperty(nameof(Person.Mother))!.Children.First().Subject);
    }

    [Fact]
    public void WhenRemovingSelfReference_ThenSubjectIsDetached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        var person = new Person(context)
        {
            FirstName = "Narcissus"
        };
        person.Father = person; // Self-reference

        // Act - Remove self-reference
        person.Father = null;

        // Assert - Subject is removed (ReferenceCount = 0 means final detach)
        Assert.Empty(registry.KnownSubjects);
    }

    [Fact]
    public void WhenBreakingCycle_ThenChildrenCollectionIsUpdated()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        var person1 = new Person(context)
        {
            FirstName = "Alice"
        };

        var person2 = new Person(context)
        {
            FirstName = "Bob",
            Mother = person1
        };

        person1.Mother = person2; // Create cycle

        // Verify cycle is established
        var person1Mother = person1.TryGetRegisteredProperty(nameof(Person.Mother))!;
        var person2Mother = person2.TryGetRegisteredProperty(nameof(Person.Mother))!;
        Assert.Single(person1Mother.Children);
        Assert.Single(person2Mother.Children);

        // Act - Break the cycle by removing person1.Mother
        person1.Mother = null;

        // Assert - Both removed: person2's ref=0 triggers cascade that also removes person1
        // (When person2 is removed, its properties including person2.Mother→person1 are detached)
        Assert.Empty(registry.KnownSubjects);
    }

    [Fact]
    public void WhenCreatingCycleThroughCollection_ThenAllSubjectsAreRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act - Create cycle where parent is in its own Children collection
        var parent = new Person(context)
        {
            FirstName = "Parent"
        };

        var child = new Person(context)
        {
            FirstName = "Child"
        };

        parent.Children = [child, parent]; // Parent includes itself in children

        // Assert
        Assert.Equal(2, registry.KnownSubjects.Count);

        var registeredParent = registry.TryGetRegisteredSubject(parent)!;
        var childrenProperty = registeredParent.TryGetProperty(nameof(Person.Children))!;

        // Should have 2 children: child and parent itself
        Assert.Equal(2, childrenProperty.Children.Length);
        Assert.Contains(childrenProperty.Children, c => c.Subject == child);
        Assert.Contains(childrenProperty.Children, c => c.Subject == parent);

        // Verify indices
        var parentInChildren = childrenProperty.Children.First(c => c.Subject == parent);
        Assert.Equal(1, parentInChildren.Index);
    }

    [Fact]
    public void WhenCreatingComplexMultiCycle_ThenAllRelationshipsAreTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act - Create complex graph with multiple interconnected cycles:
        // Cycle 1: Alice ↔ Bob (via Mother/Father)
        // Cycle 2: Charlie ↔ Diana (via Mother)
        // Bridge: Bob → Charlie
        var alice = new Person(context) { FirstName = "Alice" };
        var bob = new Person(context) { FirstName = "Bob" };
        var charlie = new Person(context) { FirstName = "Charlie" };
        var diana = new Person(context) { FirstName = "Diana" };

        alice.Mother = bob;
        bob.Father = alice;
        bob.Mother = charlie;
        charlie.Mother = diana;
        diana.Mother = charlie;

        // Assert - All subjects are registered
        Assert.Equal(4, registry.KnownSubjects.Count);

        var registeredAlice = registry.TryGetRegisteredSubject(alice)!;
        var registeredBob = registry.TryGetRegisteredSubject(bob)!;
        var registeredCharlie = registry.TryGetRegisteredSubject(charlie)!;
        var registeredDiana = registry.TryGetRegisteredSubject(diana)!;

        Assert.NotNull(registeredAlice);
        Assert.NotNull(registeredBob);
        Assert.NotNull(registeredCharlie);
        Assert.NotNull(registeredDiana);

        // Verify Cycle 1: Alice ↔ Bob
        var aliceMotherProperty = registeredAlice.TryGetProperty(nameof(Person.Mother))!;
        Assert.Single(aliceMotherProperty.Children);
        Assert.Equal(bob, aliceMotherProperty.Children.First().Subject);

        var bobFatherProperty = registeredBob.TryGetProperty(nameof(Person.Father))!;
        Assert.Single(bobFatherProperty.Children);
        Assert.Equal(alice, bobFatherProperty.Children.First().Subject);

        // Verify Bridge: Bob → Charlie
        var bobMotherProperty = registeredBob.TryGetProperty(nameof(Person.Mother))!;
        Assert.Single(bobMotherProperty.Children);
        Assert.Equal(charlie, bobMotherProperty.Children.First().Subject);

        // Verify Cycle 2: Charlie ↔ Diana
        var charlieMotherProperty = registeredCharlie.TryGetProperty(nameof(Person.Mother))!;
        Assert.Single(charlieMotherProperty.Children);
        Assert.Equal(diana, charlieMotherProperty.Children.First().Subject);

        var dianaMotherProperty = registeredDiana.TryGetProperty(nameof(Person.Mother))!;
        Assert.Single(dianaMotherProperty.Children);
        Assert.Equal(charlie, dianaMotherProperty.Children.First().Subject);

        // Verify parent-child relationships are bidirectional
        Assert.Contains(registeredBob.Parents, p => p.Property.Parent.Subject == alice);
        Assert.Contains(registeredAlice.Parents, p => p.Property.Parent.Subject == bob);
        Assert.Contains(registeredCharlie.Parents, p => p.Property.Parent.Subject == bob);
        Assert.Contains(registeredDiana.Parents, p => p.Property.Parent.Subject == charlie);
        Assert.Contains(registeredCharlie.Parents, p => p.Property.Parent.Subject == diana);
    }

    [Fact]
    public void WhenTraversingCyclicGraph_ThenGetAllPropertiesDoesNotHang()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        // Act - Create cycle
        var person1 = new Person(context) { FirstName = "Person1" };
        var person2 = new Person(context) { FirstName = "Person2" };

        person1.Mother = person2;
        person2.Mother = person1;

        var registered1 = registry.TryGetRegisteredSubject(person1)!;

        // Act - GetAllProperties traverses the cyclic graph
        var allProperties = registered1.GetAllProperties().ToList();

        // Assert - Should complete without hanging due to cycle detection
        Assert.NotEmpty(allProperties);
    }

    [Fact]
    public void WhenReplacingCyclicReference_ThenChildrenAreUpdatedCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        var person1 = new Person(context) { FirstName = "Person1" };
        var person2 = new Person(context) { FirstName = "Person2" };
        var person3 = new Person(context) { FirstName = "Person3" };

        // Create cycle: person1 ↔ person2
        person1.Mother = person2;
        person2.Mother = person1;

        // After cycle: person1 (ref=1), person2 (ref=1), person3 (ref=0, still in registry - not detached yet)
        var subjectsAfterCycle = string.Join(", ", registry.KnownSubjects.Keys.Select(s => ((Person)s).FirstName));
        Assert.True(registry.KnownSubjects.Count == 3, $"After cycle: Expected 3 but found {registry.KnownSubjects.Count}. Actual: {subjectsAfterCycle}");

        // Act - Replace person1.Mother with person3 (breaking cycle)
        person1.Mother = person3;

        // Assert - person2 removed (ref=0). person1 and person3 remain.
        // (person1 gets re-added when person3 is attached as parent)
        var actualSubjects = string.Join(", ", registry.KnownSubjects.Keys.Select(s => ((Person)s).FirstName));
        Assert.True(registry.KnownSubjects.Count == 2, $"Expected 2 subjects but found {registry.KnownSubjects.Count}. Actual: {actualSubjects}");
        Assert.Contains(person1, registry.KnownSubjects.Keys);
        Assert.Contains(person3, registry.KnownSubjects.Keys);
    }
}
