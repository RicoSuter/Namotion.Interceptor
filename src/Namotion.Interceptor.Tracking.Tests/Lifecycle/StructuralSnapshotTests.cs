using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class StructuralSnapshotTests
{
    [Fact]
    public void WhenDictionaryKeyEqualityThrows_ThenLifecycleUsesSubjectOrdinals()
    {
        // Arrange
        var reconcileContext = InterceptorSubjectContext.Create().WithLifecycle();
        var reconcileHolder = new BroadPropertyHolder(reconcileContext);
        var reconciledChild = new Car();
        reconcileHolder.Payload = new Dictionary<ThrowingKey, Car> { [new ThrowingKey(1)] = reconciledChild };

        var lookupContext = InterceptorSubjectContext.Create().WithLifecycle();
        var lookupHolder = new BroadPropertyHolder(lookupContext);
        var lookedUpChild = new Car();
        lookupHolder.Payload = new Dictionary<ThrowingKey, Car> { [new ThrowingKey(2)] = lookedUpChild };

        var rekeyContext = InterceptorSubjectContext.Create().WithLifecycle();
        var rekeyHolder = new BroadPropertyHolder(rekeyContext);
        var rekeyedChild = new Car();
        rekeyHolder.Payload = new Dictionary<ThrowingKey, Car> { [new ThrowingKey(3)] = rekeyedChild };

        var releaseContext = InterceptorSubjectContext.Create().WithLifecycle();
        var releaseHolder = new BroadPropertyHolder(releaseContext);
        var releasedChild = new Car();
        releaseHolder.Payload = new Dictionary<ThrowingKey, Car> { [new ThrowingKey(5)] = releasedChild };

        // Act
        var reconcileException = Record.Exception(() => reconcileHolder.Payload = new Dictionary<ThrowingKey, Car>());
        var lookupException = Record.Exception(() => _ = lookedUpChild.GetParents());
        var rekeyException = Record.Exception(() =>
            rekeyHolder.Payload = new Dictionary<ThrowingKey, Car> { [new ThrowingKey(4)] = rekeyedChild });
        var releaseException = Record.Exception(() => releaseHolder.DetachFromContext(releaseContext));

        // Assert
        Assert.Null(reconcileException);
        Assert.Null(lookupException);
        Assert.Null(rekeyException);
        Assert.Null(releaseException);
        Assert.Equal(2, ((ThrowingKey)lookedUpChild.GetParents()[0].Index!).Value);
        Assert.Equal(4, ((ThrowingKey)rekeyedChild.GetParents()[0].Index!).Value);
        Assert.Null(reconciledChild.TryGetContext());
        Assert.Null(releasedChild.TryGetContext());
    }

    [Fact]
    public void WhenACommittedMutableValueChangesWithoutASetter_ThenReleaseUsesTheCommittedSnapshot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var holder = new EnumerableChildrenHolder(context);
        var committedChild = new Person();
        var uncommittedChild = new Person();
        var list = new List<Person> { committedChild };
        holder.Children = list;
        list.Clear();
        list.Add(uncommittedChild);

        // Act
        holder.DetachFromContext(context);

        // Assert
        Assert.Null(committedChild.TryGetContext());
        Assert.Null(uncommittedChild.TryGetContext());
    }

    [Fact]
    public void WhenTheValueIsASubject_ThenTheSnapshotContainsItsDirectOccurrence()
    {
        // Arrange
        var child = new Person();

        // Act
        var snapshot = StructuralSnapshotBuilder.Build(typeof(Person), child, 17);

        // Assert
        Assert.Equal(17, snapshot.SourceRevision);
        var occurrence = Assert.Single(snapshot.Occurrences);
        Assert.Same(child, occurrence.Subject);
        Assert.Equal(0, occurrence.SubjectOrdinal);
        Assert.Null(occurrence.Index);
    }

    [Fact]
    public void WhenACollectionContainsDuplicateSubjects_ThenOrdinalsArePerSubjectIdentity()
    {
        // Arrange
        var first = new Person();
        var second = new Person();
        var value = new object[] { first, "ignored", second, first, second, first };

        // Act
        var snapshot = StructuralSnapshotBuilder.Build(typeof(object[]), value, 18);

        // Assert
        Assert.Equal(18, snapshot.SourceRevision);
        Assert.Collection(
            snapshot.Occurrences,
            occurrence => AssertOccurrence(occurrence, first, 0, 0),
            occurrence => AssertOccurrence(occurrence, second, 0, 2),
            occurrence => AssertOccurrence(occurrence, first, 1, 3),
            occurrence => AssertOccurrence(occurrence, second, 1, 4),
            occurrence => AssertOccurrence(occurrence, first, 2, 5));
    }

    [Fact]
    public void WhenTheValueIsAGenericDictionary_ThenTheSnapshotContainsKeyedOccurrences()
    {
        // Arrange
        var first = new Person();
        var second = new Person();
        var value = new Dictionary<string, Person>
        {
            ["first"] = first,
            ["duplicate"] = first,
            ["second"] = second
        };

        // Act
        var snapshot = StructuralSnapshotBuilder.Build(typeof(Dictionary<string, Person>), value, 19);

        // Assert
        Assert.Collection(
            snapshot.Occurrences,
            occurrence => AssertOccurrence(occurrence, first, 0, "first"),
            occurrence => AssertOccurrence(occurrence, first, 1, "duplicate"),
            occurrence => AssertOccurrence(occurrence, second, 0, "second"));
    }

    [Fact]
    public void WhenTheValueIsANonGenericDictionary_ThenTheSnapshotContainsKeyedOccurrences()
    {
        // Arrange
        var first = new Person();
        var second = new Person();
        var value = new Hashtable
        {
            ["first"] = first,
            ["duplicate"] = first,
            ["second"] = second
        };

        // Act
        var snapshot = StructuralSnapshotBuilder.Build(typeof(IDictionary), value, 20);

        // Assert
        Assert.Equal(3, snapshot.Occurrences.Length);
        var firstOccurrences = snapshot.Occurrences.Where(occurrence => ReferenceEquals(occurrence.Subject, first)).ToArray();
        Assert.Equal([0, 1], firstOccurrences.Select(occurrence => occurrence.SubjectOrdinal).Order());
        Assert.Equal(["duplicate", "first"], firstOccurrences.Select(occurrence => occurrence.Index).Cast<string>().Order());
        AssertOccurrence(snapshot.Occurrences.Single(occurrence => Equals(occurrence.Index, "second")), second, 0, "second");
    }

    [Fact]
    public void WhenTheValueIsAReadOnlyDictionary_ThenTheSnapshotContainsKeyedOccurrences()
    {
        // Arrange
        var first = new Person();
        var second = new Person();
        var value = new SnapshotReadOnlyDictionary<string, Person>(
            new Dictionary<string, Person>
            {
                ["first"] = first,
                ["duplicate"] = first,
                ["second"] = second
            });

        // Act
        var snapshot = StructuralSnapshotBuilder.Build(typeof(IReadOnlyDictionary<string, Person>), value, 21);

        // Assert
        Assert.Collection(
            snapshot.Occurrences,
            occurrence => AssertOccurrence(occurrence, first, 0, "first"),
            occurrence => AssertOccurrence(occurrence, first, 1, "duplicate"),
            occurrence => AssertOccurrence(occurrence, second, 0, "second"));
    }

    private static void AssertOccurrence(
        StructuralOccurrence occurrence,
        IInterceptorSubject subject,
        int subjectOrdinal,
        object? index)
    {
        Assert.Same(subject, occurrence.Subject);
        Assert.Equal(subjectOrdinal, occurrence.SubjectOrdinal);
        Assert.Equal(index, occurrence.Index);
    }

    private sealed class SnapshotReadOnlyDictionary<TKey, TValue>(Dictionary<TKey, TValue> entries)
        : IReadOnlyDictionary<TKey, TValue> where TKey : notnull
    {
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int Count => entries.Count;

        public bool ContainsKey(TKey key) => entries.ContainsKey(key);

        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => entries.TryGetValue(key, out value);

        public TValue this[TKey key] => entries[key];

        public IEnumerable<TKey> Keys => entries.Keys;

        public IEnumerable<TValue> Values => entries.Values;
    }

    private readonly struct ThrowingKey(int value) : IEquatable<ThrowingKey>
    {
        public int Value { get; } = value;

        public bool Equals(ThrowingKey other) => throw new InvalidOperationException("Dictionary key equality was invoked.");

        public override bool Equals(object? obj) => throw new InvalidOperationException("Dictionary key equality was invoked.");

        public override int GetHashCode() => Value;
    }
}
