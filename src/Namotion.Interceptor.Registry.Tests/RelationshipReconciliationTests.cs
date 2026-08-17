using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

public class RelationshipReconciliationTests
{
    [Theory]
    [InlineData(RelationshipShape.Direct)]
    [InlineData(RelationshipShape.Array)]
    [InlineData(RelationshipShape.MutableList)]
    [InlineData(RelationshipShape.Collection)]
    [InlineData(RelationshipShape.Dictionary)]
    [InlineData(RelationshipShape.ReadOnlyDictionary)]
    [InlineData(RelationshipShape.EnumerableFallback)]
    [InlineData(RelationshipShape.MixedContent)]
    public void WhenASupportedShapeIsAttached_ThenEveryRelationshipViewAgrees(RelationshipShape shape)
    {
        // Removing full relationship publication for any supported shape would leave at least one view
        // empty, out of order, or carrying a different index.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        var container = new RelationshipShapeContainer(context);

        // Act
        var (propertyName, expected) = shape switch
        {
            RelationshipShape.Direct => SetDirect(container, first),
            RelationshipShape.Array => SetArray(container, first, second),
            RelationshipShape.MutableList => SetMutableList(container, first, second),
            RelationshipShape.Collection => SetCollection(container, first, second),
            RelationshipShape.Dictionary => SetDictionary(container, first, second),
            RelationshipShape.ReadOnlyDictionary => SetReadOnlyDictionary(container, first, second),
            RelationshipShape.EnumerableFallback => SetEnumerable(container, first, second),
            RelationshipShape.MixedContent => SetMixed(container, first, second),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };

        // Assert
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        AssertRelationshipViews(container, registry, property, expected);
    }

    [Fact]
    public void WhenAnArrayIsInsertedRemovedReorderedAndReplaced_ThenEveryViewTracksEachGeneration()
    {
        // Patching retained entries in place would strand removals or preserve an old order after one of
        // these transitions.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        var third = new Person { FirstName = "C" };
        var replacement = new Person { FirstName = "D" };
        var container = new RelationshipShapeContainer(context)
        {
            Array = [first, third]
        };
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(RelationshipShapeContainer.Array))!;

        // Act & Assert: insertion
        container.Array = [first, second, third];
        AssertRelationshipViews(container, registry, property,
            [(first, 0), (second, 1), (third, 2)]);

        // Act & Assert: reorder
        container.Array = [third, second, first];
        AssertRelationshipViews(container, registry, property,
            [(third, 0), (second, 1), (first, 2)]);

        // Act & Assert: removal
        container.Array = [third, first];
        AssertRelationshipViews(container, registry, property,
            [(third, 0), (first, 1)]);
        AssertDetached(registry, second);

        // Act & Assert: replacement
        container.Array = [third, replacement];
        AssertRelationshipViews(container, registry, property,
            [(third, 0), (replacement, 1)]);
        AssertDetached(registry, first);
    }

    [Fact]
    public void WhenDuplicateOccurrencesAreReordered_ThenExactOrderAndCapturedSnapshotsStayFrozen()
    {
        // Collapsing by child membership would lose the second occurrence. Mutating cached projections in
        // place would also make the captured arrays acquire the new generation's order or indices.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        var container = new RelationshipShapeContainer(context)
        {
            Array = [first, second, first]
        };
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(RelationshipShapeContainer.Array))!;
        var oldChildren = property.Children;
        var oldParents = first.TryGetRegisteredSubject()!.Parents;
        var oldTrackedParents = first.GetParents();

        // Act
        container.Array = [first, first, second];

        // Assert
        AssertRelationshipViews(container, registry, property,
            [(first, 0), (first, 1), (second, 2)]);
        Assert.Equal([first, second, first], oldChildren.Select(child => child.Subject));
        Assert.Equal([0, 1, 2], oldChildren.Select(child => child.Index));
        Assert.Equal([0, 2], oldParents.Select(parent => parent.Index));
        Assert.Equal([0, 2], oldTrackedParents.Select(parent => parent.Index));
    }

    [Fact]
    public void WhenADuplicateDictionaryGroupIsRekeyed_ThenSnapshotsAndSingularPathUseTheirOwnGeneration()
    {
        // Reusing mutable child records would combine an old array order with new keys. Selecting a stale
        // membership entry would also keep the old first key in the singular path.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var shared = new Person { FirstName = "Shared" };
        var other = new Person { FirstName = "Other" };
        var directory = new PersonDirectory(context)
        {
            PeopleByName = new Dictionary<string, Person>
            {
                ["alpha"] = shared,
                ["beta"] = shared,
                ["other"] = other
            }
        };
        var property = directory.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersonDirectory.PeopleByName))!;
        var oldChildren = property.Children;
        var oldParents = shared.TryGetRegisteredSubject()!.Parents;

        // Act
        directory.PeopleByName = new Dictionary<string, Person>
        {
            ["gamma"] = shared,
            ["delta"] = shared,
            ["other"] = other
        };

        // Assert
        AssertRelationshipViews(directory, registry, property,
            [(shared, "gamma"), (shared, "delta"), (other, "other")]);
        Assert.Equal(["alpha", "beta", "other"], oldChildren.Select(child => child.Index));
        Assert.Equal(["alpha", "beta"], oldParents.Select(parent => parent.Index));

        var nameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;
        Assert.Equal("PeopleByName[gamma].FirstName",
            nameProperty.TryGetPath(DefaultPathProvider.Instance, directory));
    }

    [Fact]
    public void WhenTheSameEnumerableFailsAfterYielding_ThenNoPartialViewIsPublishedAndRetrySucceeds()
    {
        // Applying each yielded item directly would expose a mixed generation after the exception. Treating
        // same-instance reassignment as a no-op would then prevent the canonical retry.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var oldFirst = new Person { FirstName = "OldA" };
        var oldSecond = new Person { FirstName = "OldB" };
        var newFirst = new Person { FirstName = "NewA" };
        var newSecond = new Person { FirstName = "NewB" };
        var container = new RelationshipShapeContainer(context)
        {
            Enumerable = Enumerate(oldFirst, oldSecond)
        };
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(RelationshipShapeContainer.Enumerable))!;
        var oldChildren = property.Children;
        var oldParents = oldFirst.TryGetRegisteredSubject()!.Parents;
        var throwing = new ThrowOnceEnumerable<Person>(newFirst, newSecond);

        // Act & Assert: failed enumeration
        Assert.Throws<InvalidOperationException>(() => container.Enumerable = throwing);
        AssertRelationshipViews(container, registry, property,
            [(oldFirst, 0), (oldSecond, 1)],
            assertPath: false);
        Assert.Equal([oldFirst, oldSecond], oldChildren.Select(child => child.Subject));
        Assert.Equal([0], oldParents.Select(parent => parent.Index));
        AssertDetached(registry, newFirst);
        AssertDetached(registry, newSecond);

        // Act: successful same-instance retry
        container.Enumerable = throwing;

        // Assert
        AssertRelationshipViews(container, registry, property,
            [(newFirst, 0), (newSecond, 1)]);
        AssertDetached(registry, oldFirst);
        AssertDetached(registry, oldSecond);
    }

    [Fact]
    public void WhenOpaqueDictionaryKeysAreReconciled_ThenTheirEqualityAndHashingAreNeverCalled()
    {
        // Any subject-keyed or relationship-keyed dictionary using the opaque key as a comparer input would
        // throw before the views can be published.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        var alpha = new OpaqueKey("alpha");
        var beta = new OpaqueKey("beta");
        var gamma = new OpaqueKey("gamma");
        var directory = new OpaqueKeyDirectory(context)
        {
            Items = new OpaqueReadOnlyDictionary((alpha, first), (beta, second))
        };
        var property = directory.TryGetRegisteredSubject()!.TryGetProperty(nameof(OpaqueKeyDirectory.Items))!;

        // Act
        var exception = Record.Exception(() =>
            directory.Items = new OpaqueReadOnlyDictionary((beta, second), (gamma, first)));

        // Assert
        Assert.Null(exception);
        AssertRelationshipViews(directory, registry, property,
            [(second, beta), (first, gamma)]);
        Assert.Same(gamma, first.TryGetRegisteredSubject()!.Parents[0].Index);
        Assert.Same(gamma, first.GetParents().Single().Index);
    }

    [Fact]
    public void WhenAReentrantDictionaryKeyIsArmed_ThenReconciliationDoesNotCompareIt()
    {
        // The former registry refresh compared retained dictionary keys. The relationship sequence must carry
        // the enumerated key reference as opaque metadata without invoking caller code.
        // Arrange
        var context = CreateContext();
        var child = new Person { FirstName = "Child" };
        var equalityWasCalled = false;
        var key = new ReentrantKey("alpha", () => equalityWasCalled = true);
        var items = new Dictionary<ReentrantKey, Person> { [key] = child };
        var container = new RelationshipShapeContainer(context)
        {
            ReentrantDictionary = items
        };
        key.Arm();

        // Act
        container.ReentrantDictionary = items;

        // Assert
        Assert.False(equalityWasCalled);
        var property = container.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(RelationshipShapeContainer.ReentrantDictionary))!;
        Assert.Same(key, property.Children.Single().Index);
        Assert.Same(key, child.TryGetRegisteredSubject()!.Parents.Single().Index);
    }

    [Fact]
    public void WhenDistinctSubjectsAreEqualByValue_ThenTheyRegisterAndReconcileIndependently()
    {
        // Default subject equality would collapse the second subject in known-subject state or temporary
        // reconciliation lookups.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var first = new EqualByValueItem { Tag = "A" };
        var second = new EqualByValueItem { Tag = "B" };
        var directory = new PersonDirectory(context)
        {
            Untyped = new IInterceptorSubject[] { first, second }
        };
        var property = directory.TryGetRegisteredSubject()!.TryGetProperty(nameof(PersonDirectory.Untyped))!;

        // Act
        directory.Untyped = new IInterceptorSubject[] { second, first };

        // Assert
        Assert.Equal([second, first], property.Children.Select(child => child.Subject));
        Assert.Equal([0, 1], property.Children.Select(child => child.Index));
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
        Assert.Equal(1, registry.KnownSubjects.Keys.Count(subject => ReferenceEquals(subject, first)));
        Assert.Equal(1, registry.KnownSubjects.Keys.Count(subject => ReferenceEquals(subject, second)));
        Assert.Same(first, registry.TryGetRegisteredSubject(first)!.Subject);
        Assert.Same(second, registry.TryGetRegisteredSubject(second)!.Subject);
        Assert.Equal(1, first.TryGetRegisteredSubject()!.Parents.Single().Index);
        Assert.Equal(0, second.TryGetRegisteredSubject()!.Parents.Single().Index);
    }

    [Fact]
    public void WhenAParentGroupIsRekeyed_ThenOtherPropertyGroupsKeepTheirAttachmentOrder()
    {
        // Removing and appending the changed group would move it behind an unrelated group and change the
        // singular parent path.
        // Arrange
        var context = CreateContext();
        var shared = new Person { FirstName = "Shared" };
        var directory = new PersonDirectory(context)
        {
            PeopleByName = new Dictionary<string, Person>
            {
                ["alpha"] = shared,
                ["beta"] = shared
            }
        };
        var otherParent = new Person(context) { Father = shared };

        // Act
        directory.PeopleByName = new Dictionary<string, Person>
        {
            ["gamma"] = shared,
            ["delta"] = shared
        };

        // Assert
        var parents = shared.TryGetRegisteredSubject()!.Parents;
        Assert.Equal(3, parents.Length);
        Assert.Equal(nameof(PersonDirectory.PeopleByName), parents[0].Property.Name);
        Assert.Equal("gamma", parents[0].Index);
        Assert.Equal(nameof(PersonDirectory.PeopleByName), parents[1].Property.Name);
        Assert.Equal("delta", parents[1].Index);
        Assert.Equal(nameof(Person.Father), parents[2].Property.Name);
        Assert.Null(parents[2].Index);
        Assert.Same(otherParent, parents[2].Property.Subject);

        var nameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;
        Assert.Equal("PeopleByName[gamma].FirstName",
            nameProperty.TryGetPath(DefaultPathProvider.Instance, directory));
    }

    [Fact]
    public void WhenAContextDetachesAndReattaches_ThenRelationshipGroupsClearAndReturnExactly()
    {
        // Leaving outgoing or incoming groups behind on detach would duplicate or reorder them on reattach.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var shared = new Person { FirstName = "Shared" };
        var container = new RelationshipShapeContainer
        {
            Array = [shared, shared]
        };
        var subjectContext = ((IInterceptorSubject)container).Context;
        subjectContext.AddFallbackContext(context);
        var property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(RelationshipShapeContainer.Array))!;
        AssertRelationshipViews(container, registry, property, [(shared, 0), (shared, 1)]);

        // Act: detach
        subjectContext.RemoveFallbackContext(context);

        // Assert
        Assert.Empty(registry.KnownSubjects);
        AssertDetached(registry, shared);
        Assert.Empty(property.Children);

        // Act: reattach
        subjectContext.AddFallbackContext(context);

        // Assert
        property = container.TryGetRegisteredSubject()!.TryGetProperty(nameof(RelationshipShapeContainer.Array))!;
        AssertRelationshipViews(container, registry, property, [(shared, 0), (shared, 1)]);
    }

    [Fact]
    public void WhenCyclesAndSelfReferencesAreAttached_ThenMembershipAndOccurrencesRemainFinite()
    {
        // Traversing or grouping by value semantics would recurse or duplicate membership for these edges.
        // Arrange
        var context = CreateContext();
        var registry = context.GetService<ISubjectRegistry>();
        var first = new Person(context) { FirstName = "A" };
        var second = new Person { FirstName = "B" };

        // Act
        first.Father = first;
        first.Mother = second;
        second.Father = first;

        // Assert: self-reference
        var father = first.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Father))!;
        AssertRelationshipViews(first, registry, father, [(first, null)],
            assertPath: false, assertSingleMembership: false);

        // Assert: cycle
        var mother = first.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Mother))!;
        AssertRelationshipViews(first, registry, mother, [(second, null)]);
        var secondFather = second.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Father))!;
        AssertRelationshipViews(second, registry, secondFather, [(first, null)],
            assertPath: false, assertSingleMembership: false);
        Assert.Equal(2, registry.KnownSubjects.Count);
        Assert.Equal(2, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
    }

    [Fact]
    public void WhenDuplicateMembershipAttachesAndDetaches_ThenLifecycleEventsSeeProvisionalAndCompleteGroups()
    {
        // Publishing no provisional relationship would hide the ancestor during SubjectAttached. Clearing
        // the group before SubjectDetaching would hide duplicate occurrences from detach observers.
        // Arrange
        var context = CreateContext();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var container = new RelationshipShapeContainer(context);
        var child = new Person { FirstName = "Child" };
        object?[]? parentsDuringAttach = null;
        object?[]? childrenDuringAttach = null;
        object?[]? parentsDuringDetach = null;
        object?[]? childrenDuringDetach = null;

        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                var property = container.TryGetRegisteredSubject()!
                    .TryGetProperty(nameof(RelationshipShapeContainer.Array))!;
                parentsDuringAttach = child.TryGetRegisteredSubject()!.Parents.Select(parent => parent.Index).ToArray();
                childrenDuringAttach = property.Children.Select(relationship => relationship.Index).ToArray();
            }
        };
        lifecycle.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                var property = container.TryGetRegisteredSubject()!
                    .TryGetProperty(nameof(RelationshipShapeContainer.Array))!;
                parentsDuringDetach = child.TryGetRegisteredSubject()!.Parents.Select(parent => parent.Index).ToArray();
                childrenDuringDetach = property.Children.Select(relationship => relationship.Index).ToArray();
            }
        };

        // Act: attach and then detach the membership
        container.Array = [child, child];
        var finalAttachedParents = child.TryGetRegisteredSubject()!.Parents.Select(parent => parent.Index).ToArray();
        container.Array = [];

        // Assert
        Assert.Equal([0], parentsDuringAttach);
        Assert.Equal([0], childrenDuringAttach);
        Assert.Equal([0, 1], finalAttachedParents);
        Assert.Equal([0, 1], parentsDuringDetach);
        Assert.Equal([0, 1], childrenDuringDetach);
        Assert.Null(child.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenProvisionalRelationshipsChange_ThenExactSnapshotsRemainFrozenUntilFullReplacement()
    {
        // Collapsing committed duplicates, appending retry tombstones, or mutating a cached projection would
        // change at least one exact subject/key sequence captured across these provisional transitions.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        var parent = new OpaqueKeyDirectory(context);
        var duplicate = new Person { FirstName = "Duplicate" };
        var retained = new Person { FirstName = "Retained" };
        var removed = new Person { FirstName = "Removed" };
        var firstAddition = new Person { FirstName = "First addition" };
        var secondAddition = new Person { FirstName = "Second addition" };
        var firstDuplicateKey = new OpaqueKey("duplicate-first");
        var retainedKey = new OpaqueKey("retained");
        var secondDuplicateKey = new OpaqueKey("duplicate-second");
        var removedKey = new OpaqueKey("removed");
        var firstAdditionKey = new OpaqueKey("first-addition");
        var secondAdditionKey = new OpaqueKey("second-addition");

        var committedRelationships = AssignAndGetGeneration(
            relationshipHandler,
            () => parent.Items = new OpaqueReadOnlyDictionary(
                (firstDuplicateKey, duplicate),
                (retainedKey, retained),
                (secondDuplicateKey, duplicate),
                (removedKey, removed)));
        var additionRelationships = AssignAndGetGeneration(
            relationshipHandler,
            () => parent.Items = new OpaqueReadOnlyDictionary(
                (firstAdditionKey, firstAddition),
                (secondAdditionKey, secondAddition)));
        var finalRelationships = AssignAndGetGeneration(
            relationshipHandler,
            () => parent.Items = new OpaqueReadOnlyDictionary(
                (firstAdditionKey, firstAddition),
                (firstDuplicateKey, duplicate),
                (retainedKey, retained),
                (secondDuplicateKey, duplicate),
                (secondAdditionKey, secondAddition)));
        var property = new RegisteredSubject(parent)
            .TryGetProperty(nameof(OpaqueKeyDirectory.Items))!;
        property.ReplaceChildRelationships(committedRelationships.ToImmutableArray());
        var committedSnapshot = property.Children;

        // Act & Assert: remove one committed membership while duplicates and a retained membership stay visible.
        property.RemoveChildRelationships(removed);
        var removalSnapshot = property.Children;
        AssertChildren(removalSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey));

        // Act & Assert: provisional additions append in lifecycle order.
        property.AddChildRelationship(additionRelationships[0]);
        property.AddChildRelationship(additionRelationships[1]);
        var provisionalSnapshot = property.Children;
        AssertChildren(provisionalSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (firstAddition, firstAdditionKey),
            (secondAddition, secondAdditionKey));

        // Act & Assert: replacing an active addition moves one node to the end without duplicating it.
        property.AddChildRelationship(additionRelationships[0]);
        var replacedAdditionSnapshot = property.Children;
        AssertChildren(replacedAdditionSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (secondAddition, secondAdditionKey),
            (firstAddition, firstAdditionKey));

        // Act & Assert: removing and re-adding an addition unlinks it and appends one active node.
        property.RemoveChildRelationships(firstAddition);
        var provisionalRemovalSnapshot = property.Children;
        AssertChildren(provisionalRemovalSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (secondAddition, secondAdditionKey));
        property.AddChildRelationship(additionRelationships[0]);
        var readdedSnapshot = property.Children;
        AssertChildren(readdedSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (secondAddition, secondAdditionKey),
            (firstAddition, firstAdditionKey));

        // Act & Assert: re-adding a removed committed duplicate suppresses both old occurrences.
        property.RemoveChildRelationships(duplicate);
        var duplicateRemovalSnapshot = property.Children;
        AssertChildren(duplicateRemovalSnapshot,
            (retained, retainedKey),
            (secondAddition, secondAdditionKey),
            (firstAddition, firstAdditionKey));
        property.AddChildRelationship(committedRelationships[0]);
        var duplicateReaddedSnapshot = property.Children;
        AssertChildren(duplicateReaddedSnapshot,
            (retained, retainedKey),
            (secondAddition, secondAdditionKey),
            (firstAddition, firstAdditionKey),
            (duplicate, firstDuplicateKey));

        // Act: full publication replaces the complete group and clears the provisional overlay.
        property.ReplaceChildRelationships(finalRelationships.ToImmutableArray());
        var finalSnapshot = property.Children;

        // Assert
        AssertChildren(finalSnapshot,
            (firstAddition, firstAdditionKey),
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (secondAddition, secondAdditionKey));
        AssertChildren(committedSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (removed, removedKey));
        AssertChildren(removalSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey));
        AssertChildren(provisionalSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (firstAddition, firstAdditionKey),
            (secondAddition, secondAdditionKey));
        AssertChildren(replacedAdditionSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (secondAddition, secondAdditionKey),
            (firstAddition, firstAdditionKey));
        AssertChildren(provisionalRemovalSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (secondAddition, secondAdditionKey));
        AssertChildren(readdedSnapshot,
            (duplicate, firstDuplicateKey),
            (retained, retainedKey),
            (duplicate, secondDuplicateKey),
            (secondAddition, secondAdditionKey),
            (firstAddition, firstAdditionKey));
        AssertChildren(duplicateRemovalSnapshot,
            (retained, retainedKey),
            (secondAddition, secondAdditionKey),
            (firstAddition, firstAdditionKey));
        AssertChildren(duplicateReaddedSnapshot,
            (retained, retainedKey),
            (secondAddition, secondAdditionKey),
            (firstAddition, firstAdditionKey),
            (duplicate, firstDuplicateKey));
    }

    private static IInterceptorSubjectContext CreateContext() => InterceptorSubjectContext
        .Create()
        .WithParents()
        .WithRegistry();

    private static (string PropertyName, (Person Child, object? Index)[] Relationships) SetDirect(
        RelationshipShapeContainer container,
        Person first)
    {
        container.Direct = first;
        return (nameof(RelationshipShapeContainer.Direct), [(first, null)]);
    }

    private static (string PropertyName, (Person Child, object? Index)[] Relationships) SetArray(
        RelationshipShapeContainer container,
        Person first,
        Person second)
    {
        container.Array = [first, second];
        return (nameof(RelationshipShapeContainer.Array), [(first, 0), (second, 1)]);
    }

    private static (string PropertyName, (Person Child, object? Index)[] Relationships) SetMutableList(
        RelationshipShapeContainer container,
        Person first,
        Person second)
    {
        container.MutableList = [first, second];
        return (nameof(RelationshipShapeContainer.MutableList), [(first, 0), (second, 1)]);
    }

    private static (string PropertyName, (Person Child, object? Index)[] Relationships) SetCollection(
        RelationshipShapeContainer container,
        Person first,
        Person second)
    {
        container.Collection = new Collection<Person> { first, second };
        return (nameof(RelationshipShapeContainer.Collection), [(first, 0), (second, 1)]);
    }

    private static (string PropertyName, (Person Child, object? Index)[] Relationships) SetDictionary(
        RelationshipShapeContainer container,
        Person first,
        Person second)
    {
        container.Dictionary = new Dictionary<string, Person> { ["alpha"] = first, ["beta"] = second };
        return (nameof(RelationshipShapeContainer.Dictionary), [(first, "alpha"), (second, "beta")]);
    }

    private static (string PropertyName, (Person Child, object? Index)[] Relationships) SetReadOnlyDictionary(
        RelationshipShapeContainer container,
        Person first,
        Person second)
    {
        container.ReadOnlyDictionary = new ReadOnlyPersonDictionary(
            new Dictionary<string, Person> { ["alpha"] = first, ["beta"] = second });
        return (nameof(RelationshipShapeContainer.ReadOnlyDictionary), [(first, "alpha"), (second, "beta")]);
    }

    private static (string PropertyName, (Person Child, object? Index)[] Relationships) SetEnumerable(
        RelationshipShapeContainer container,
        Person first,
        Person second)
    {
        container.Enumerable = Enumerate(first, second);
        return (nameof(RelationshipShapeContainer.Enumerable), [(first, 0), (second, 1)]);
    }

    private static (string PropertyName, (Person Child, object? Index)[] Relationships) SetMixed(
        RelationshipShapeContainer container,
        Person first,
        Person second)
    {
        container.Mixed = EnumerateMixed("ignored", first, null, second);
        return (nameof(RelationshipShapeContainer.Mixed), [(first, 1), (second, 3)]);
    }

    private static IEnumerable<Person> Enumerate(params Person[] people)
    {
        foreach (var person in people)
        {
            yield return person;
        }
    }

    private static IEnumerable<object?> EnumerateMixed(params object?[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
    }

    private static void AssertRelationshipViews<TParent>(
        TParent root,
        ISubjectRegistry registry,
        RegisteredSubjectProperty property,
        IReadOnlyList<(Person Child, object? Index)> expected,
        bool assertPath = true,
        bool assertSingleMembership = true)
        where TParent : class, IInterceptorSubject
    {
        var children = property.Children;
        Assert.Equal(expected.Count, children.Length);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index].Child, children[index].Subject);
            AssertIndex(expected[index].Index, children[index].Index);
        }

        foreach (var childGroup in expected.GroupBy(
                     relationship => relationship.Child,
                     (IEqualityComparer<Person>)ReferenceEqualityComparer.Instance))
        {
            var child = childGroup.Key;
            var expectedIndexes = childGroup.Select(relationship => relationship.Index).ToArray();
            var registeredChild = registry.TryGetRegisteredSubject(child);
            Assert.NotNull(registeredChild);
            Assert.Equal(1, registry.KnownSubjects.Keys.Count(subject => ReferenceEquals(subject, child)));
            if (assertSingleMembership)
            {
                Assert.Equal(1, registeredChild.ReferenceCount);
            }

            var registryParents = registeredChild.Parents
                .Where(parent => ReferenceEquals(parent.Property, property))
                .ToArray();
            Assert.Equal(expectedIndexes.Length, registryParents.Length);
            for (var index = 0; index < expectedIndexes.Length; index++)
            {
                AssertIndex(expectedIndexes[index], registryParents[index].Index);
            }

            var trackedParents = child.GetParents()
                .Where(parent => ReferenceEquals(parent.Property.Subject, property.Subject) &&
                                 parent.Property.Name == property.Name)
                .ToArray();
            Assert.Equal(expectedIndexes.Length, trackedParents.Length);
            for (var index = 0; index < expectedIndexes.Length; index++)
            {
                AssertIndex(expectedIndexes[index], trackedParents[index].Index);
            }

            if (assertPath)
            {
                var nameProperty = registeredChild.TryGetProperty(nameof(Person.FirstName))!;
                var firstIndex = expectedIndexes[0];
                var expectedPath = firstIndex is null
                    ? $"{property.Name}.{nameof(Person.FirstName)}"
                    : $"{property.Name}[{firstIndex}].{nameof(Person.FirstName)}";
                Assert.Equal(expectedPath,
                    nameProperty.TryGetPath(DefaultPathProvider.Instance, root));
            }
        }
    }

    private static void AssertDetached(ISubjectRegistry registry, IInterceptorSubject subject)
    {
        Assert.Null(registry.TryGetRegisteredSubject(subject));
        Assert.Equal(0, subject.GetReferenceCount());
        Assert.Empty(subject.GetParents());
        Assert.DoesNotContain(registry.KnownSubjects.Keys, known => ReferenceEquals(known, subject));
    }

    private static SubjectPropertyRelationship[] AssignAndGetGeneration(
        RecordingRelationshipHandler relationshipHandler,
        Action assignment)
    {
        relationshipHandler.Generations.Clear();
        assignment();
        return Assert.Single(relationshipHandler.Generations);
    }

    private static void AssertChildren(
        ImmutableArray<SubjectPropertyChild> actual,
        params (IInterceptorSubject Subject, object Index)[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Same(expected[index].Subject, actual[index].Subject);
            Assert.Same(expected[index].Index, actual[index].Index);
        }
    }

    private static void AssertIndex(object? expected, object? actual)
    {
        if (expected is OpaqueKey)
        {
            Assert.Same(expected, actual);
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    public enum RelationshipShape
    {
        Direct,
        Array,
        MutableList,
        Collection,
        Dictionary,
        ReadOnlyDictionary,
        EnumerableFallback,
        MixedContent
    }

    private sealed class ThrowOnceEnumerable<T>(params T[] items) : IEnumerable<T>
    {
        private bool _throwOnNextEnumeration = true;

        public IEnumerator<T> GetEnumerator()
        {
            var throwAfterFirst = _throwOnNextEnumeration;
            _throwOnNextEnumeration = false;
            return Enumerate(throwAfterFirst).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<T> Enumerate(bool throwAfterFirst)
        {
            for (var index = 0; index < items.Length; index++)
            {
                yield return items[index];
                if (throwAfterFirst && index == 0)
                {
                    throw new InvalidOperationException("Enumeration failed after yielding one item.");
                }
            }
        }
    }

    private sealed class RecordingRelationshipHandler : IPropertyRelationshipHandler
    {
        public List<SubjectPropertyRelationship[]> Generations { get; } = [];

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            if (property.Subject is OpaqueKeyDirectory &&
                property.Name == nameof(OpaqueKeyDirectory.Items))
            {
                Generations.Add(relationships.ToArray());
            }
        }
    }

    public sealed class OpaqueKey(string value)
    {
        public override bool Equals(object? obj) =>
            throw new InvalidOperationException("Key equality is forbidden.");

        public override int GetHashCode() =>
            throw new InvalidOperationException("Key hashing is forbidden.");

        public override string ToString() => value;
    }

    private sealed class OpaqueReadOnlyDictionary(params (OpaqueKey Key, Person Value)[] items)
        : IReadOnlyDictionary<OpaqueKey, Person>
    {
        public int Count => items.Length;

        public IEnumerable<OpaqueKey> Keys => items.Select(item => item.Key);

        public IEnumerable<Person> Values => items.Select(item => item.Value);

        public Person this[OpaqueKey key] => throw new NotSupportedException();

        public bool ContainsKey(OpaqueKey key) => throw new NotSupportedException();

        public bool TryGetValue(OpaqueKey key, out Person value) => throw new NotSupportedException();

        public IEnumerator<KeyValuePair<OpaqueKey, Person>> GetEnumerator() =>
            items.Select(item => KeyValuePair.Create(item.Key, item.Value)).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

[InterceptorSubject]
public partial class RelationshipShapeContainer
{
    public RelationshipShapeContainer()
    {
        Array = [];
        MutableList = [];
        Collection = new Collection<Person>();
        Dictionary = new Dictionary<string, Person>();
        ReadOnlyDictionary = new ReadOnlyPersonDictionary(new Dictionary<string, Person>());
        ReentrantDictionary = new Dictionary<ReentrantKey, Person>();
        Enumerable = [];
        Mixed = [];
    }

    public partial Person? Direct { get; set; }

    public partial Person[] Array { get; set; }

    public partial List<Person> MutableList { get; set; }

    public partial ICollection<Person> Collection { get; set; }

    public partial IReadOnlyDictionary<string, Person> Dictionary { get; set; }

    public partial IReadOnlyDictionary<string, Person> ReadOnlyDictionary { get; set; }

    public partial IReadOnlyDictionary<ReentrantKey, Person> ReentrantDictionary { get; set; }

    public partial IEnumerable<Person> Enumerable { get; set; }

    public partial IEnumerable<object?> Mixed { get; set; }
}

[InterceptorSubject]
public partial class OpaqueKeyDirectory
{
    public OpaqueKeyDirectory()
    {
        Items = new Dictionary<RelationshipReconciliationTests.OpaqueKey, Person>();
    }

    public partial IReadOnlyDictionary<RelationshipReconciliationTests.OpaqueKey, Person> Items { get; set; }
}
