using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class DictionaryIndexTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string propertyName)
        => subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;

    [Fact]
    public void WhenDictionaryEntryIsReKeyed_ThenParentIndexIsTheNewKey()
    {
        // Arrange
        var child = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Motor"] = child }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Engine"] = child };

        // Assert
        var parent = Assert.Single(child.TryGetRegisteredSubject()!.Parents);
        Assert.Equal("Engine", parent.Index);
    }

    [Fact]
    public void WhenDictionaryEntryIsReKeyed_ThenChildIsListedUnderTheNewKey()
    {
        // Arrange
        var child = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Motor"] = child }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Engine"] = child };

        // Assert
        var registeredChild = Assert.Single(GetProperty(container, nameof(TestContainer.Items)).Children);
        Assert.Same(child, registeredChild.Subject);
        Assert.Equal("Engine", registeredChild.Index);
    }

    [Fact]
    public void WhenCollectionPropertyHoldsAStringKeyedDictionaryWithoutDictionaryInterface_ThenIndexIsTheNewKey()
    {
        // Arrange: a collection refresh first, so that this thread holds a reusable position map,
        // which is what used to let a refresh read string keys as positions.
        var context = CreateContext();
        var warmFirst = new TestItem();
        var warmSecond = new TestItem();
        var warm = new TestKeyedContainer(context) { Items = [warmFirst, warmSecond] };
        warm.Items = [warmSecond, warmFirst];

        var first = new TestItem();
        var second = new TestItem();
        var holder = new BroadlyDeclaredHolder(context)
        {
            Sequence = new PairEnumeratingDictionary<string>(new Dictionary<string, TestItem> { ["First"] = first, ["Second"] = second })
        };

        // Act
        holder.Sequence = new PairEnumeratingDictionary<string>(new Dictionary<string, TestItem> { ["Second"] = first, ["First"] = second });

        // Assert
        Assert.Equal("Second", Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal("First", Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenDictionaryEntryIsReKeyed_ThenPathUsesTheNewKey()
    {
        // Arrange
        var child = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Motor"] = child }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Engine"] = child };

        // Assert
        var path = GetProperty(child, nameof(TestItem.Value)).TryGetPath(DefaultPathProvider.Instance, null);
        Assert.Equal("Items[Engine].Value", path);
    }

    [Fact]
    public void WhenDictionaryEntryIsReKeyedNextToOtherEntries_ThenOnlyItsIndexChanges()
    {
        // Arrange
        var first = new TestItem();
        var child = new TestItem();
        var last = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["First"] = first, ["Motor"] = child, ["Last"] = last }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["First"] = first, ["Last"] = last, ["Engine"] = child };

        // Assert
        var children = GetProperty(container, nameof(TestContainer.Items)).Children;
        Assert.Equal(3, children.Length);
        Assert.Equal("First", children.Single(c => c.Subject == first).Index);
        Assert.Equal("Engine", children.Single(c => c.Subject == child).Index);
        Assert.Equal("Last", children.Single(c => c.Subject == last).Index);

        Assert.Equal("First", Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal("Engine", Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal("Last", Assert.Single(last.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenTwoDictionaryKeysAreSwapped_ThenBothIndicesAreSwapped()
    {
        // Arrange
        var left = new TestItem();
        var right = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Left"] = left, ["Right"] = right }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Left"] = right, ["Right"] = left };

        // Assert
        var children = GetProperty(container, nameof(TestContainer.Items)).Children;
        Assert.Equal(2, children.Length);
        Assert.Equal("Right", children.Single(c => c.Subject == left).Index);
        Assert.Equal("Left", children.Single(c => c.Subject == right).Index);

        Assert.Equal("Right", Assert.Single(left.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal("Left", Assert.Single(right.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenReKeyedDictionaryEntryIsRemoved_ThenItsParentAndChildEntriesAreRemoved()
    {
        // Arrange: the holder keeps the child registered after it leaves the container's
        // dictionary, so an entry left behind under the old key stays observable.
        var child = new TestItem();
        var holder = new TestItem { Children = new Dictionary<string, TestItem> { ["Shared"] = child } };
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Holder"] = holder, ["Motor"] = child }
        };

        container.Items = new Dictionary<string, TestItem> { ["Holder"] = holder, ["Engine"] = child };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Holder"] = holder };

        // Assert
        var registeredChild = Assert.Single(GetProperty(container, nameof(TestContainer.Items)).Children);
        Assert.Same(holder, registeredChild.Subject);

        var parent = Assert.Single(child.TryGetRegisteredSubject()!.Parents);
        Assert.Equal(nameof(TestItem.Children), parent.Property.Name);
        Assert.Equal("Shared", parent.Index);
    }

    [Fact]
    public void WhenSubjectSitsUnderTwoDictionaryKeys_ThenItIsRegisteredOnceUnderTheFirstKey()
    {
        // Arrange
        var child = new TestItem();

        // Act
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Motor"] = child, ["Engine"] = child }
        };

        // Assert
        var registeredChild = Assert.Single(GetProperty(container, nameof(TestContainer.Items)).Children);
        Assert.Equal("Motor", registeredChild.Index);
        Assert.Equal("Motor", Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenRegisteredKeyOfSubjectUnderTwoKeysIsRemoved_ThenIndexIsTheRemainingKey()
    {
        // Arrange
        var child = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Motor"] = child, ["Engine"] = child }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Engine"] = child };

        // Assert
        var registeredChild = Assert.Single(GetProperty(container, nameof(TestContainer.Items)).Children);
        Assert.Equal("Engine", registeredChild.Index);
        Assert.Equal("Engine", Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenSecondKeyIsAddedForSubject_ThenIndexIsTheFirstKeyOfTheNewDictionary()
    {
        // Arrange
        var child = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Engine"] = child }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Motor"] = child, ["Engine"] = child };

        // Assert: the same result as attaching the new dictionary from scratch.
        var registeredChild = Assert.Single(GetProperty(container, nameof(TestContainer.Items)).Children);
        Assert.Equal("Motor", registeredChild.Index);
        Assert.Equal("Motor", Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenSubjectUnderTwoDictionaryKeysIsRemoved_ThenItsParentAndChildEntriesAreRemoved()
    {
        // Arrange: the holder keeps the child registered after it leaves the container's dictionary.
        var child = new TestItem();
        var holder = new TestItem { Children = new Dictionary<string, TestItem> { ["Shared"] = child } };
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Holder"] = holder, ["Motor"] = child, ["Engine"] = child }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Holder"] = holder };

        // Assert
        var registeredChild = Assert.Single(GetProperty(container, nameof(TestContainer.Items)).Children);
        Assert.Same(holder, registeredChild.Subject);

        var parent = Assert.Single(child.TryGetRegisteredSubject()!.Parents);
        Assert.Equal(nameof(TestItem.Children), parent.Property.Name);
    }

    [Fact]
    public void WhenKeyChangesOnlyInCaseUnderCaseInsensitiveComparer_ThenIndexIsTheNewKey()
    {
        // Arrange
        var child = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem>(StringComparer.OrdinalIgnoreCase) { ["motor"] = child }
        };

        // Act
        container.Items = new Dictionary<string, TestItem>(StringComparer.OrdinalIgnoreCase) { ["Motor"] = child };

        // Assert
        Assert.Equal("Motor", Assert.Single(GetProperty(container, nameof(TestContainer.Items)).Children).Index);
        Assert.Equal("Motor", Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenIntegerKeyedDictionaryEntryIsReKeyed_ThenIndexIsTheNewKey()
    {
        // Arrange
        var child = new TestItem();
        var container = new TestKeyedContainer(CreateContext())
        {
            ByNumber = new Dictionary<int, TestItem> { [1] = child }
        };

        // Act
        container.ByNumber = new Dictionary<int, TestItem> { [2] = child };

        // Assert
        Assert.Equal(2, Assert.Single(GetProperty(container, nameof(TestKeyedContainer.ByNumber)).Children).Index);
        Assert.Equal(2, Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenEntryOfDictionaryWithoutDictionaryInterfaceIsReKeyed_ThenIndexIsTheNewKey()
    {
        // Arrange: the wrapper implements IReadOnlyDictionary only, so its entries can only be
        // read as a sequence of key value pairs.
        var kept = new TestItem();
        var child = new TestItem();
        var container = new TestKeyedContainer(CreateContext())
        {
            ReadOnlyByNumber = new ReadOnlyOnlyDictionary(new Dictionary<int, TestItem> { [0] = kept, [1] = child })
        };

        // Act
        container.ReadOnlyByNumber = new ReadOnlyOnlyDictionary(new Dictionary<int, TestItem> { [0] = kept, [2] = child });

        // Assert
        var children = GetProperty(container, nameof(TestKeyedContainer.ReadOnlyByNumber)).Children;
        Assert.Equal(2, children.Length);
        Assert.Equal(0, children.Single(c => c.Subject == kept).Index);
        Assert.Equal(2, children.Single(c => c.Subject == child).Index);
        Assert.Equal(2, Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenDictionaryEntryIsAddedOrRemoved_ThenRetainedEntriesKeepTheirKeys()
    {
        // Arrange
        var first = new TestItem();
        var second = new TestItem();
        var third = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["First"] = first, ["Second"] = second }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Second"] = second, ["Third"] = third };

        // Assert
        var children = GetProperty(container, nameof(TestContainer.Items)).Children;
        Assert.Equal(2, children.Length);
        Assert.Equal("Second", children.Single(c => c.Subject == second).Index);
        Assert.Equal("Third", children.Single(c => c.Subject == third).Index);
        Assert.Equal("Second", Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Null(first.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenReorderedDictionaryRepeatsASubject_ThenItIsListedOnceAtItsFirstKey()
    {
        // Arrange
        var first = new TestItem();
        var second = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["First"] = first, ["Second"] = second }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Second"] = second, ["First"] = first, ["Alias"] = second };

        // Assert
        var children = GetProperty(container, nameof(TestContainer.Items)).Children;
        Assert.Equal(2, children.Length);
        Assert.Equal("Second", children.Single(c => c.Subject == second).Index);
        Assert.Equal("First", children.Single(c => c.Subject == first).Index);
        Assert.Equal("Second", Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenComparingDictionaryKeysThrows_ThenChildAndParentIndicesAgree()
    {
        // Arrange: the comparer keeps the dictionary itself from calling the throwing Equals.
        var first = new TestItem();
        var second = new TestItem();
        var container = new TestKeyedContainer(CreateContext())
        {
            ByAnything = new Dictionary<object, TestItem>(ReferenceEqualityComparer.Instance)
            {
                [new ThrowingKey()] = second, ["First"] = first
            }
        };

        // A snapshot read before the write, which a partial update must not leave behind.
        _ = GetProperty(container, nameof(TestKeyedContainer.ByAnything)).Children;

        // Act & Assert: both entries change their place, and comparing the keys of the second throws.
        Assert.Throws<NotSupportedException>(() =>
            container.ByAnything = new Dictionary<object, TestItem>(ReferenceEqualityComparer.Instance)
            {
                ["Renamed"] = first, [new ThrowingKey()] = second
            });

        var children = GetProperty(container, nameof(TestKeyedContainer.ByAnything)).Children;
        Assert.Equal(2, children.Length);
        Assert.Equal(Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index, children.Single(c => c.Subject == first).Index);
        Assert.Equal(Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index, children.Single(c => c.Subject == second).Index);
    }

    [Fact]
    public void WhenIntegerKeyedDictionaryEntryIsReKeyed_ThenChildrenKeepTheirAttachOrder()
    {
        // Arrange
        var first = new TestItem();
        var second = new TestItem();
        var container = new TestKeyedContainer(CreateContext())
        {
            ByNumber = new Dictionary<int, TestItem> { [1] = first, [2] = second }
        };

        // Act
        container.ByNumber = new Dictionary<int, TestItem> { [3] = first, [2] = second };

        // Assert
        var children = GetProperty(container, nameof(TestKeyedContainer.ByNumber)).Children;
        Assert.Equal([first, second], children.Select(c => c.Subject));
        Assert.Equal([3, 2], children.Select(c => c.Index));
    }

    [Fact]
    public void WhenDictionaryKeyDoesNotEqualItself_ThenParentIndexStillFollowsTheReKey()
    {
        // Arrange: the comparer keeps the dictionary itself from calling the broken Equals.
        var child = new TestItem();
        var newKey = new NeverEqualKey();
        var container = new TestKeyedContainer(CreateContext())
        {
            ByAnything = new Dictionary<object, TestItem>(ReferenceEqualityComparer.Instance) { [new NeverEqualKey()] = child }
        };

        // Act
        container.ByAnything = new Dictionary<object, TestItem>(ReferenceEqualityComparer.Instance) { [newKey] = child };

        // Assert
        Assert.Same(newKey, Assert.Single(GetProperty(container, nameof(TestKeyedContainer.ByAnything)).Children).Index);
        Assert.Same(newKey, Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenBroadlyDeclaredCollectionIsReordered_ThenIndicesAreTheNewPositions()
    {
        // Arrange: the property is declared as object, so only the value says it is a collection.
        var first = new TestItem();
        var second = new TestItem();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            Items = new List<TestItem> { first, second }
        };

        // Act
        holder.Items = new List<TestItem> { second, first };

        // Assert
        var children = GetProperty(holder, nameof(BroadlyDeclaredHolder.Items)).Children;
        Assert.Equal([second, first], children.Select(c => c.Subject));
        Assert.Equal([0, 1], children.Select(c => c.Index));
        Assert.Equal(1, Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal(0, Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenBroadlyDeclaredDictionaryHasIntegerKeysOutOfOrder_ThenChildrenAreOrderedByKey()
    {
        // Arrange
        var first = new TestItem();
        var second = new TestItem();
        var holder = new BroadlyDeclaredHolder(CreateContext());

        // Act
        holder.Items = new Dictionary<int, TestItem> { [5] = first, [1] = second };

        // Assert
        var children = GetProperty(holder, nameof(BroadlyDeclaredHolder.Items)).Children;
        Assert.Equal([second, first], children.Select(c => c.Subject));
        Assert.Equal([1, 5], children.Select(c => c.Index));
    }

    [Fact]
    public void WhenBroadlyDeclaredCollectionGivesWayToItsOnlyItem_ThenIndexIsNull()
    {
        // Arrange
        var child = new TestItem();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            Items = new List<TestItem> { child }
        };

        // Act
        holder.Items = child;

        // Assert
        Assert.Null(Assert.Single(GetProperty(holder, nameof(BroadlyDeclaredHolder.Items)).Children).Index);
        Assert.Null(Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenBroadlyDeclaredDictionaryBecomesACollection_ThenIndicesArePositions()
    {
        // Arrange
        var first = new TestItem();
        var second = new TestItem();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["First"] = first, ["Second"] = second }
        };

        // Act
        holder.Items = new List<TestItem> { second, first };

        // Assert
        var children = GetProperty(holder, nameof(BroadlyDeclaredHolder.Items)).Children;
        Assert.Equal([second, first], children.Select(c => c.Subject));
        Assert.Equal([0, 1], children.Select(c => c.Index));
        Assert.Equal(1, Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenUntypedCollectionPropertyHoldsAReKeyedDictionary_ThenIndexIsTheNewKey()
    {
        // Arrange: a non-generic ICollection declaration classifies as a collection, the value is keyed.
        var first = new TestItem();
        var second = new TestItem();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            Untyped = new Dictionary<string, TestItem> { ["First"] = first, ["Second"] = second }
        };

        // Act
        holder.Untyped = new Dictionary<string, TestItem> { ["Second"] = first, ["First"] = second };

        // Assert
        Assert.Equal("Second", Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal("First", Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenCollectionPropertyHoldsAReKeyedDictionaryWithoutDictionaryInterface_ThenIndexIsTheNewKey()
    {
        // Arrange: declared as a sequence of subjects, while the value's own type is a dictionary
        // that enumerates as key value pairs.
        var first = new TestItem();
        var second = new TestItem();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            Sequence = new PairEnumeratingDictionary<int>(new Dictionary<int, TestItem> { [1] = first, [2] = second })
        };

        // Act
        holder.Sequence = new PairEnumeratingDictionary<int>(new Dictionary<int, TestItem> { [2] = first, [1] = second });

        // Assert
        Assert.Equal(2, Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal(1, Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenEntryOfBroadlyDeclaredDictionaryIsReKeyed_ThenIndexIsTheNewKey()
    {
        // Arrange: the property is declared as object, so only the value says it is a dictionary.
        var child = new TestItem();
        var holder = new BroadlyDeclaredHolder(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Motor"] = child }
        };

        // Act
        holder.Items = new Dictionary<string, TestItem> { ["Engine"] = child };

        // Assert
        Assert.Equal("Engine", Assert.Single(GetProperty(holder, nameof(BroadlyDeclaredHolder.Items)).Children).Index);
        Assert.Equal("Engine", Assert.Single(child.TryGetRegisteredSubject()!.Parents).Index);
    }

    [Fact]
    public void WhenDictionaryEnumeratingAsItsValuesIsReordered_ThenIndicesAreTheNewPositions()
    {
        // Arrange: a dictionary type that enumerates as its values is attached by position.
        var first = new TestItem();
        var second = new TestItem();
        var container = new TestKeyedContainer(CreateContext())
        {
            ReadOnlyByNumber = new ValueEnumeratingDictionary(new Dictionary<int, TestItem> { [10] = first, [20] = second })
        };

        // Act
        container.ReadOnlyByNumber = new ValueEnumeratingDictionary(new Dictionary<int, TestItem> { [20] = second, [10] = first });

        // Assert
        var children = GetProperty(container, nameof(TestKeyedContainer.ReadOnlyByNumber)).Children;
        Assert.Equal(1, children.Single(c => c.Subject == first).Index);
        Assert.Equal(0, children.Single(c => c.Subject == second).Index);
        Assert.Equal(1, Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal(0, Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index);
    }

    private sealed class ThrowingKey
    {
        public override bool Equals(object? obj) => throw new NotSupportedException();

        public override int GetHashCode() => 0;
    }

    private sealed class NeverEqualKey
    {
        public override bool Equals(object? obj) => false;

        public override int GetHashCode() => 0;
    }

    private sealed class PairEnumeratingDictionary<TKey>(Dictionary<TKey, TestItem> items)
        : IReadOnlyDictionary<TKey, TestItem>, IEnumerable<TestItem>
        where TKey : notnull
    {
        public TestItem this[TKey key] => items[key];

        public IEnumerable<TKey> Keys => items.Keys;

        public IEnumerable<TestItem> Values => items.Values;

        public int Count => items.Count;

        public bool ContainsKey(TKey key) => items.ContainsKey(key);

        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TestItem value) => items.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<TKey, TestItem>> GetEnumerator() => items.GetEnumerator();

        IEnumerator<TestItem> IEnumerable<TestItem>.GetEnumerator() => items.Values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ReadOnlyOnlyDictionary(Dictionary<int, TestItem> items) : IReadOnlyDictionary<int, TestItem>
    {
        public TestItem this[int key] => items[key];

        public IEnumerable<int> Keys => items.Keys;

        public IEnumerable<TestItem> Values => items.Values;

        public int Count => items.Count;

        public bool ContainsKey(int key) => items.ContainsKey(key);

        public bool TryGetValue(int key, [MaybeNullWhen(false)] out TestItem value) => items.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<int, TestItem>> GetEnumerator() => items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ValueEnumeratingDictionary(Dictionary<int, TestItem> items)
        : IReadOnlyDictionary<int, TestItem>, IEnumerable<TestItem>
    {
        public TestItem this[int key] => items[key];

        public IEnumerable<int> Keys => items.Keys;

        public IEnumerable<TestItem> Values => items.Values;

        public int Count => items.Count;

        public bool ContainsKey(int key) => items.ContainsKey(key);

        public bool TryGetValue(int key, [MaybeNullWhen(false)] out TestItem value) => items.TryGetValue(key, out value);

        public IEnumerator<TestItem> GetEnumerator() => items.Values.GetEnumerator();

        IEnumerator<KeyValuePair<int, TestItem>> IEnumerable<KeyValuePair<int, TestItem>>.GetEnumerator() => items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

[InterceptorSubject]
public partial class BroadlyDeclaredHolder
{
    public partial object? Items { get; set; }

    public partial ICollection? Untyped { get; set; }

    public partial IEnumerable<TestItem>? Sequence { get; set; }
}
