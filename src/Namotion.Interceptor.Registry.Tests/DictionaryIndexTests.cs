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
    public partial ICollection? Untyped { get; set; }

    public partial IEnumerable<TestItem>? Sequence { get; set; }
}
