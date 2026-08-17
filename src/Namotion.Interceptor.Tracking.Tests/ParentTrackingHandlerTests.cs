using System.Reflection;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class ParentTrackingHandlerTests
{
    private const string ParentsDataKey = "Namotion.Interceptor.Tracking.Parents";

    [Fact]
    public void WhenReferencedByTwoPropertiesOfTheSameParent_ThenTwoReferencesAreSet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var parent = new Person(context)
        {
            FirstName = "Parent"
        };

        var person = new Person(context);
        person.FirstName = "Child";
        person.Mother = parent;
        person.Father = parent;

        // Assert
        var parents = parent.GetParents();
        Assert.Equal(2, parents.Length);
    }

    [Fact]
    public void WhenReferencesAreSetToNull_ThenParentIsEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var parent = new Person(context)
        {
            FirstName = "Parent"
        };

        var person = new Person(context);
        person.FirstName = "Child";
        person.Mother = parent;
        person.Father = parent;

        person.Mother = null;
        person.Father = null;

        // Assert
        var parents = parent.GetParents();
        Assert.Empty(parents);
    }

    [Fact]
    public void WhenReferencedByTwoOtherSubjects_ThenItHasTwoParents()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var mother = new Person(context);
        mother.FirstName = "Mother";

        var child1 = new Person(context);
        child1.FirstName = "Child1";
        child1.Mother = mother;

        var child2 = new Person(context)
        {
            FirstName = "Child2",
            Mother = mother
        };

        // Assert
        var parents = mother.GetParents();
        Assert.Equal(2, parents.Length);
    }

    [Fact]
    public void WhenACollectionIsReorderedWithoutTheRegistry_ThenTheTrackedIndexMoves()
    {
        // Parent tracking keeps its own copy of each index, so it has to follow a reorder on its own,
        // without the registry being registered.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };

        var parent = new Person(context) { Children = [first, second] };

        // Act
        parent.Children = [second, first];

        // Assert
        Assert.Equal(1, first.GetParents().Single().Index);
        Assert.Equal(0, second.GetParents().Single().Index);
    }

    [Fact]
    public void WhenDictionaryContainsTheSameChildUnderTwoKeys_ThenBothParentsKeepExactSourceOrder()
    {
        // Collapsing relationships by parent property would lose the second occurrence.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var child = new Car { Name = "child" };
        var garage = new Garage(context);

        // Act
        garage.CarsByName = new Dictionary<string, Car>
        {
            ["alpha"] = child,
            ["beta"] = child
        };

        // Assert
        var parents = child.GetParents();
        Assert.Equal(2, parents.Length);
        Assert.Equal(["alpha", "beta"], parents.Select(parent => parent.Index));
        Assert.All(parents, parent => Assert.Equal(nameof(Garage.CarsByName), parent.Property.Name));
    }

    [Fact]
    public void WhenOneDuplicateOccurrenceIsRemoved_ThenTheRemainingOccurrenceIsTracked()
    {
        // Removing by property membership would discard the retained duplicate as well.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var child = new Car { Name = "child" };
        var garage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car>
            {
                ["alpha"] = child,
                ["beta"] = child
            }
        };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["beta"] = child };

        // Assert
        var parent = Assert.Single(child.GetParents());
        Assert.Equal("beta", parent.Index);
    }

    [Fact]
    public void WhenDuplicateOccurrencesAreReordered_ThenParentsFollowExactSourceOrder()
    {
        // Updating one mutable index per property would collapse both occurrences into one entry.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var child = new Car { Name = "child" };
        var other = new Car { Name = "other" };
        var garage = new Garage(context) { CarArray = [child, other, child] };

        // Act
        garage.CarArray = [other, child, child];

        // Assert
        Assert.Equal([1, 2], child.GetParents().Select(parent => parent.Index));
    }

    [Fact]
    public void WhenOpaqueDictionaryOccurrenceIsRekeyed_ThenParentTrackingDoesNotUseKeyEquality()
    {
        // Comparing old and new keys by value would invoke user equality during reconciliation.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var firstKey = new HostileKey();
        var secondKey = new HostileKey();
        var child = new Car { Name = "child" };
        var garage = new Garage(context)
        {
            CarsByOpaqueKey = new IdentityReadOnlyDictionary<object, Car>((firstKey, child))
        };

        // Act
        garage.CarsByOpaqueKey = new IdentityReadOnlyDictionary<object, Car>((secondKey, child));
        var resolvedParent = child.TryGetFirstParent<Garage>();

        // Assert
        Assert.Same(secondKey, Assert.Single(child.GetParents()).Index);
        Assert.Same(garage, resolvedParent);
    }

    [Fact]
    public void WhenAParentGroupIsRekeyed_ThenOtherParentGroupsKeepAttachmentOrder()
    {
        // Replacing a group by remove-and-append would change which current parent is first.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var child = new Car { Name = "child" };
        var firstGarage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["first"] = child }
        };
        var secondGarage = new Garage(context)
        {
            CarsByName = new Dictionary<string, Car> { ["second"] = child }
        };

        // Act
        firstGarage.CarsByName = new Dictionary<string, Car> { ["moved"] = child };

        // Assert
        var parents = child.GetParents();
        Assert.Equal(2, parents.Length);
        Assert.Same(firstGarage, parents[0].Property.Subject);
        Assert.Equal("moved", parents[0].Index);
        Assert.Same(secondGarage, parents[1].Property.Subject);
        Assert.Equal("second", parents[1].Index);
        Assert.Same(firstGarage, child.TryGetFirstParent<Garage>());
    }

    [Fact]
    public void WhenAParentGroupIsReconciled_ThenPreviouslyReturnedSnapshotsStayFrozen()
    {
        // Mutating relationship indices in place would retroactively alter a captured snapshot.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var child = new Car { Name = "child" };
        var firstOther = new Car { Name = "first other" };
        var secondOther = new Car { Name = "second other" };
        var garage = new Garage(context) { CarArray = [child, firstOther, child] };
        var oldSnapshot = child.GetParents();

        // Act
        garage.CarArray = [firstOther, child, secondOther, child];
        var newSnapshot = child.GetParents();

        // Assert
        Assert.Equal([0, 2], oldSnapshot.Select(parent => parent.Index));
        Assert.Equal([1, 3], newSnapshot.Select(parent => parent.Index));
    }

    [Fact]
    public void WhenDistinctParentsCompareEqual_ThenTraversalUsesReferenceIdentityAndCurrentOrder()
    {
        // A value-equality visited set would discard the second, matching parent as a duplicate.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var child = new Person { FirstName = "child" };
        var firstParent = new ValueEqualPerson(context) { Children = [child] };
        var secondParent = new MatchingValueEqualPerson(context) { Children = [child] };

        // Act
        var matchingParent = child.TryGetFirstParent<MatchingValueEqualPerson>();
        firstParent.Children = [];
        var firstCurrentParent = child.TryGetFirstParent<Person>();

        // Assert
        Assert.Same(secondParent, matchingParent);
        Assert.Same(secondParent, firstCurrentParent);
    }

    [Fact]
    public async Task WhenReaderBuildsFirstCacheWhileRelationshipsMove_ThenItSeesOneCompleteGeneration()
    {
        // Publishing a cache from mutable relationships could combine an old occurrence order with moved indices.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var child = new Car { Name = "child" };
        var firstOther = new Car { Name = "first other" };
        var secondOther = new Car { Name = "second other" };
        var garage = new Garage(context) { CarArray = [child, firstOther, child] };
        var viewLock = GetParentViewLock(child);
        using var readerStarted = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        Thread? readerThread = null;
        Thread? writerThread = null;
        Task<IReadOnlyList<object?>> readerTask;
        Task writerTask;

        // Act
        using (viewLock.EnterScope())
        {
            readerTask = Task.Run(() =>
            {
                readerThread = Thread.CurrentThread;
                readerStarted.Set();
                return (IReadOnlyList<object?>)child.GetParents().Select(parent => parent.Index).ToArray();
            });
            readerStarted.Wait();
            SpinWait.SpinUntil(
                () => readerThread is not null && readerThread.ThreadState.HasFlag(ThreadState.WaitSleepJoin));

            writerTask = Task.Run(() =>
            {
                writerThread = Thread.CurrentThread;
                writerStarted.Set();
                garage.CarArray = [firstOther, child, secondOther, child];
            });
            writerStarted.Wait();
            SpinWait.SpinUntil(
                () => writerThread is not null && writerThread.ThreadState.HasFlag(ThreadState.WaitSleepJoin));
        }

        var racedGeneration = await readerTask;
        await writerTask;
        var currentGeneration = child.GetParents().Select(parent => parent.Index).ToArray();

        // Assert
        Assert.True(
            racedGeneration.SequenceEqual(new object?[] { 0, 2 }) ||
            racedGeneration.SequenceEqual(new object?[] { 1, 3 }));
        Assert.Equal([1, 3], currentGeneration);
    }

    private static Lock GetParentViewLock(IInterceptorSubject subject)
    {
        Assert.True(subject.Data.TryGetValue((null, ParentsDataKey), out var storage));
        var field = storage!.GetType().GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<Lock>(field?.GetValue(storage));
    }

    private sealed class HostileKey
    {
        public override bool Equals(object? obj) =>
            throw new InvalidOperationException("Key equality is forbidden.");

        public override int GetHashCode() =>
            throw new InvalidOperationException("Key hashing is forbidden.");
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

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private class ValueEqualPerson : Person
    {
        public ValueEqualPerson(IInterceptorSubjectContext context)
            : base(context)
        {
        }

        public override bool Equals(object? obj) => obj is ValueEqualPerson;

        public override int GetHashCode() => 1;
    }

    private sealed class MatchingValueEqualPerson : ValueEqualPerson
    {
        public MatchingValueEqualPerson(IInterceptorSubjectContext context)
            : base(context)
        {
        }
    }
}
