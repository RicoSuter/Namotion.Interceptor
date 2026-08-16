using System.Collections;
using System.Collections.Specialized;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class SubjectPropertyRelationshipReconcilerTests
{
    [Fact]
    public void WhenRelationshipsAreNotRequested_ThenStateStoresOnlyDistinctMembershipMetadata()
    {
        // Allocating one relationship per duplicate in lifecycle-only configurations would defeat compact state.
        // Arrange
        var garage = new Garage();
        var property = new PropertyReference(garage, nameof(Garage.CarArray));
        var first = new Car { Name = "first" };
        var second = new Car { Name = "second" };

        // Act
        var reconciliation = SubjectPropertyRelationshipReconciler.Stage(
            property,
            new[] { first, second, first },
            previousState: null,
            materializeRelationships: false);

        // Assert
        Assert.Empty(reconciliation.State.Relationships);
        Assert.Equal(2, reconciliation.State.Memberships.Length);
        Assert.Same(first, reconciliation.State.Memberships[0].Subject);
        Assert.Equal(0, reconciliation.State.Memberships[0].FirstIndex);
        Assert.Equal(2, reconciliation.State.Memberships[0].LastIndex);
        Assert.Null(reconciliation.State.Memberships[0].FirstRelationship);
        Assert.Null(reconciliation.State.Memberships[0].LastRelationship);
        Assert.Same(second, reconciliation.State.Memberships[1].Subject);
        Assert.Equal(1, reconciliation.State.Memberships[1].FirstIndex);
        Assert.Equal(1, reconciliation.State.Memberships[1].LastIndex);
    }

    [Fact]
    public void WhenDistinctMembershipChanges_ThenRemovalsAreReverseOldOrderAndAdditionsAreSourceOrder()
    {
        // Iterating distinct membership in first-occurrence order would detach A before C even though C was last.
        // Arrange
        var garage = new Garage();
        var property = new PropertyReference(garage, nameof(Garage.CarArray));
        var first = new Car { Name = "first" };
        var second = new Car { Name = "second" };
        var third = new Car { Name = "third" };
        var fourth = new Car { Name = "fourth" };
        var fifth = new Car { Name = "fifth" };
        var previous = SubjectPropertyRelationshipReconciler.Stage(
            property,
            new[] { first, second, first, third },
            previousState: null,
            materializeRelationships: false).State;

        // Act
        var reconciliation = SubjectPropertyRelationshipReconciler.Stage(
            property,
            new[] { fourth, fifth },
            previous,
            materializeRelationships: false);

        // Assert
        Assert.Equal(
            new IInterceptorSubject[] { third, first, second },
            reconciliation.MembershipRemovals.Select(membership => membership.Subject));
        Assert.Equal(
            new IInterceptorSubject[] { fourth, fifth },
            reconciliation.MembershipAdditions.Select(membership => membership.Subject));
    }

    [Fact]
    public void WhenSupportedContainerShapesAreWritten_ThenOccurrencesKeepExactSourceOrder()
    {
        // A dispatch branch that treats dictionary entries as positions, compresses mixed-container positions,
        // or enumerates read-only dictionaries as ordinary collections would publish incorrect occurrence metadata.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var first = new Car { Name = "first" };
        var second = new Car { Name = "second" };
        var firstKey = new object();
        var secondKey = new object();
        var dictionary = new OrderedDictionary
        {
            ["ignored"] = "not a subject",
            [firstKey] = second,
            [secondKey] = first
        };
        var readOnlyDictionary = new IdentityReadOnlyDictionary<object, Car>(
            (firstKey, first),
            (secondKey, second));

        // Act & Assert
        AssertRelationships(
            AssignAndGetGeneration(relationshipHandler, () => garage.PrimaryCar = first),
            nameof(Garage.PrimaryCar),
            (first, null));
        AssertRelationships(
            AssignAndGetGeneration(relationshipHandler, () => garage.CarArray = [second, first]),
            nameof(Garage.CarArray),
            (second, 0),
            (first, 1));
        AssertRelationships(
            AssignAndGetGeneration(relationshipHandler, () => garage.MutableCars = [first, second]),
            nameof(Garage.MutableCars),
            (first, 0),
            (second, 1));
        AssertRelationships(
            AssignAndGetGeneration(
                relationshipHandler,
                () => garage.CollectionItems = new ArrayList { "ignored", first, null, second }),
            nameof(Garage.CollectionItems),
            (first, 1),
            (second, 3));
        AssertRelationships(
            AssignAndGetGeneration(
                relationshipHandler,
                () => garage.EnumerableItems = new CountingEnumerable<object?>(first, "ignored", second)),
            nameof(Garage.EnumerableItems),
            (first, 0),
            (second, 2));
        AssertRelationships(
            AssignAndGetGeneration(relationshipHandler, () => garage.DictionaryItems = dictionary),
            nameof(Garage.DictionaryItems),
            (second, firstKey),
            (first, secondKey));
        AssertRelationships(
            AssignAndGetGeneration(relationshipHandler, () => garage.CarsByOpaqueKey = readOnlyDictionary),
            nameof(Garage.CarsByOpaqueKey),
            (first, firstKey),
            (second, secondKey));
    }

    [Fact]
    public void WhenNullEmptyAndMixedValuesAreWritten_ThenOnlySubjectOccurrencesArePublished()
    {
        // Treating null, strings, or non-subject elements as children would create phantom graph memberships.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var child = new Car { Name = "child" };

        // Act & Assert
        Assert.Empty(AssignAndGetGeneration(relationshipHandler, () => garage.PrimaryCar = null));
        Assert.Empty(AssignAndGetGeneration(relationshipHandler, () => garage.CarArray = []));
        AssertRelationships(
            AssignAndGetGeneration(
                relationshipHandler,
                () => garage.CollectionItems = new ArrayList { null, "ignored", child, 42 }),
            nameof(Garage.CollectionItems),
            (child, 2));
        Assert.Empty(AssignAndGetGeneration(
            relationshipHandler,
            () => garage.EnumerableItems = new CountingEnumerable<object?>(null, "ignored", 42)));
    }

    [Fact]
    public void WhenDuplicateOccurrencesAreReorderedAndRemoved_ThenRelationshipsAreMatchedByOccurrenceAndMembershipStaysUnique()
    {
        // Matching by a distinct-child set would collapse duplicates, while attaching per occurrence would over-count membership.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var first = new Car { Name = "first" };
        var second = new Car { Name = "second" };
        garage.CarArray = [first, first, second];

        var initial = Assert.Single(relationshipHandler.Generations);
        AssertRelationships(
            initial,
            nameof(Garage.CarArray),
            (first, 0),
            (first, 1),
            (second, 2));
        first.Attachements.Clear();
        first.Detachements.Clear();
        second.Attachements.Clear();
        second.Detachements.Clear();

        // Act
        var reordered = AssignAndGetGeneration(
            relationshipHandler,
            () => garage.CarArray = [first, second, first]);

        // Assert
        AssertRelationships(
            reordered,
            nameof(Garage.CarArray),
            (first, 0),
            (second, 1),
            (first, 2));
        Assert.Same(initial[0], reordered[0]);
        Assert.NotSame(initial[2], reordered[1]);
        Assert.NotSame(initial[1], reordered[2]);
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
        Assert.Empty(first.Attachements);
        Assert.Empty(first.Detachements);
        Assert.Empty(second.Attachements);
        Assert.Empty(second.Detachements);

        var retained = AssignAndGetGeneration(relationshipHandler, () => garage.CarArray = [first]);
        Assert.Same(reordered[0], Assert.Single(retained));
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(0, second.GetReferenceCount());
        Assert.Empty(first.Detachements);
        Assert.Single(second.Detachements);
    }

    [Fact]
    public void WhenADirectChildIsRetainedReplacedAndRemoved_ThenEachGenerationIsCanonical()
    {
        // Reusing a mutable occurrence object would retroactively change the retained first generation.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var first = new Car { Name = "first" };
        var second = new Car { Name = "second" };

        // Act
        var initial = AssignAndGetGeneration(relationshipHandler, () => garage.PrimaryCar = first);
        var retained = AssignAndGetGeneration(relationshipHandler, () => garage.PrimaryCar = first);
        var replaced = AssignAndGetGeneration(relationshipHandler, () => garage.PrimaryCar = second);
        var removed = AssignAndGetGeneration(relationshipHandler, () => garage.PrimaryCar = null);

        // Assert
        var initialRelationship = Assert.Single(initial);
        Assert.Same(initialRelationship, Assert.Single(retained));
        Assert.NotSame(initialRelationship, Assert.Single(replaced));
        Assert.Same(first, initialRelationship.Child);
        Assert.Null(initialRelationship.Index);
        Assert.Empty(removed);
        Assert.Same(initialRelationship, Assert.Single(first.Attachements).Relationship);
        Assert.Same(initialRelationship, Assert.Single(first.Detachements).Relationship);
        Assert.Same(replaced[0], Assert.Single(second.Attachements).Relationship);
        Assert.Same(replaced[0], Assert.Single(second.Detachements).Relationship);
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Equal(0, second.GetReferenceCount());
    }

    [Fact]
    public void WhenDictionaryOccurrencesAreRekeyed_ThenOnlyReferenceEqualKeysReuseRelationships()
    {
        // Calling key equality or reusing by equal value would violate the opaque-key identity contract.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var child = new Car { Name = "child" };
        var firstKey = new HostileKey();
        var secondKey = new HostileKey();

        // Act
        var initial = AssignAndGetGeneration(
            relationshipHandler,
            () => garage.CarsByOpaqueKey = new IdentityReadOnlyDictionary<object, Car>((firstKey, child)));
        var retained = AssignAndGetGeneration(
            relationshipHandler,
            () => garage.CarsByOpaqueKey = new IdentityReadOnlyDictionary<object, Car>((firstKey, child)));
        var rekeyed = AssignAndGetGeneration(
            relationshipHandler,
            () => garage.CarsByOpaqueKey = new IdentityReadOnlyDictionary<object, Car>((secondKey, child)));

        // Assert
        var initialRelationship = Assert.Single(initial);
        Assert.Same(firstKey, initialRelationship.Index);
        Assert.Same(initialRelationship, Assert.Single(retained));
        Assert.NotSame(initialRelationship, Assert.Single(rekeyed));
        Assert.Same(secondKey, rekeyed[0].Index);
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenOpaqueDictionaryKeysAreRetainedAndRekeyed_ThenLifecycleCallbacksDoNotUseKeyEquality()
    {
        // Collection refresh callbacks must not compare dictionary keys after lifecycle state has committed.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);

        var garage = new Garage(context);
        var child = new Car { Name = "child" };
        var firstKey = new HostileKey();
        var secondKey = new HostileKey();
        garage.CarsByOpaqueKey = new IdentityReadOnlyDictionary<object, Car>((firstKey, child));

        // Act
        garage.CarsByOpaqueKey = new IdentityReadOnlyDictionary<object, Car>((firstKey, child));
        garage.CarsByOpaqueKey = new IdentityReadOnlyDictionary<object, Car>((secondKey, child));

        // Assert
        var relationship = Assert.Single(relationshipHandler.Generations[^1]);
        Assert.Same(secondKey, relationship.Index);
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenASetterOnlySubjectContainerIsWritten_ThenTheWrittenValueIsReconciled()
    {
        // Setter-only generated metadata has no getter, so the committed write value is the only structural input.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var child = new Car { Name = "child" };

        // Act
        garage.SetterOnlyCars = [child];

        // Assert
        AssertRelationships(
            Assert.Single(relationshipHandler.Generations),
            nameof(Garage.SetterOnlyCars),
            (child, 0));
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenAReadOnlyDictionaryAlsoImplementsICollection_ThenDictionaryKeysAndSubjectsAreRetained()
    {
        // Declared dictionary shape takes precedence over incidental non-generic collection implementation.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var child = new Car { Name = "child" };
        var key = new object();
        var dictionary = new CollectionReadOnlyDictionary<object, Car>((key, child));

        // Act
        garage.CarsByOpaqueKey = dictionary;

        // Assert
        AssertRelationships(
            Assert.Single(relationshipHandler.Generations),
            nameof(Garage.CarsByOpaqueKey),
            (child, key));
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenAHostileSubjectAppearsMoreThanOnce_ThenReferenceIdentityAvoidsItsEqualityMembers()
    {
        // Any default subject comparer in membership or occurrence matching invokes these throwing members.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var child = new HostileCar();

        // Act
        garage.CarArray = [child, child];

        // Assert
        var relationships = Assert.Single(relationshipHandler.Generations);
        AssertRelationships(
            relationships,
            nameof(Garage.CarArray),
            (child, 0),
            (child, 1));
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenAnEnumerableIsWritten_ThenItIsEnumeratedExactlyOnce()
    {
        // A second pass can observe different user state and would make staging internally inconsistent.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var child = new Car { Name = "child" };
        var enumerable = new CountingEnumerable<object?>(child, "ignored");

        // Act
        garage.EnumerableItems = enumerable;

        // Assert
        Assert.Equal(1, enumerable.EnumerationCount);
        AssertRelationships(
            Assert.Single(relationshipHandler.Generations),
            nameof(Garage.EnumerableItems),
            (child, 0));
    }

    [Fact]
    public void WhenEnumerationFails_ThenMembershipAndThePublishedGenerationStayAtThePreviousBaseline()
    {
        // Committing while enumeration is still in progress would detach the old child and leak the yielded new child.
        // Arrange
        var (garage, relationshipHandler) = CreateGarage();
        var first = new Car { Name = "first" };
        var second = new Car { Name = "second" };
        garage.EnumerableItems = [first];
        var initial = Assert.Single(relationshipHandler.Generations);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            garage.EnumerableItems = new ThrowingEnumerable<object?>(second));
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(0, second.GetReferenceCount());
        Assert.Single(relationshipHandler.Generations);
        Assert.Same(initial[0], relationshipHandler.Generations[0][0]);

        var recovered = AssignAndGetGeneration(relationshipHandler, () => garage.EnumerableItems = [second]);
        AssertRelationships(recovered, nameof(Garage.EnumerableItems), (second, 0));
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
    }

    private static (Garage Garage, RecordingRelationshipHandler Handler) CreateGarage()
    {
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        return (new Garage(context), relationshipHandler);
    }

    private static SubjectPropertyRelationship[] AssignAndGetGeneration(
        RecordingRelationshipHandler relationshipHandler,
        Action assignment)
    {
        relationshipHandler.Generations.Clear();
        assignment();
        return Assert.Single(relationshipHandler.Generations);
    }

    private static void AssertRelationships(
        SubjectPropertyRelationship[] relationships,
        string propertyName,
        params (IInterceptorSubject Child, object? Index)[] expected)
    {
        Assert.Equal(expected.Length, relationships.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(propertyName, relationships[index].Parent.Name);
            Assert.Same(expected[index].Child, relationships[index].Child);
            if (expected[index].Index is null or int)
            {
                Assert.Equal(expected[index].Index, relationships[index].Index);
            }
            else
            {
                Assert.Same(expected[index].Index, relationships[index].Index);
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
            Generations.Add(relationships.ToArray());
        }
    }

    private sealed class HostileCar : Car
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException("Subject equality is forbidden.");

        public override int GetHashCode() => throw new InvalidOperationException("Subject hashing is forbidden.");
    }

    private sealed class HostileKey
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException("Key equality is forbidden.");

        public override int GetHashCode() => throw new InvalidOperationException("Key hashing is forbidden.");
    }

    private sealed class CountingEnumerable<T>(params T[] items) : IEnumerable<T>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
            {
                throw new InvalidOperationException("The value was enumerated more than once.");
            }

            return ((IEnumerable<T>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingEnumerable<T>(T yieldedItem) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            yield return yieldedItem;
            throw new InvalidOperationException("Enumeration failed.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class IdentityReadOnlyDictionary<TKey, TValue>(params (TKey Key, TValue Value)[] entries)
        : IReadOnlyDictionary<TKey, TValue>
        where TKey : class
    {
        public int Count => entries.Length;

        public IEnumerable<TKey> Keys => entries.Select(entry => entry.Key);

        public IEnumerable<TValue> Values => entries.Select(entry => entry.Value);

        public TValue this[TKey key] => entries.First(entry => ReferenceEquals(entry.Key, key)).Value;

        public bool ContainsKey(TKey key) => entries.Any(entry => ReferenceEquals(entry.Key, key));

        public bool TryGetValue(TKey key, out TValue value)
        {
            foreach (var entry in entries)
            {
                if (ReferenceEquals(entry.Key, key))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            foreach (var entry in entries)
            {
                yield return new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CollectionReadOnlyDictionary<TKey, TValue>(params (TKey Key, TValue Value)[] entries)
        : IReadOnlyDictionary<TKey, TValue>, ICollection
        where TKey : class
    {
        public int Count => entries.Length;

        public IEnumerable<TKey> Keys => entries.Select(entry => entry.Key);

        public IEnumerable<TValue> Values => entries.Select(entry => entry.Value);

        public TValue this[TKey key] => entries.First(entry => ReferenceEquals(entry.Key, key)).Value;

        public bool IsSynchronized => false;

        public object SyncRoot { get; } = new();

        public bool ContainsKey(TKey key) => entries.Any(entry => ReferenceEquals(entry.Key, key));

        public bool TryGetValue(TKey key, out TValue value)
        {
            foreach (var entry in entries)
            {
                if (ReferenceEquals(entry.Key, key))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        public void CopyTo(Array array, int index)
        {
            foreach (var entry in entries)
            {
                array.SetValue(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value), index++);
            }
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            foreach (var entry in entries)
            {
                yield return new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
