using System.Collections;
using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class SubjectLookupTests
{
    [Fact]
    public void WhenValueIsList_ThenReturnsSubjectAtIndex()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var list = new List<Person> { new() { FirstName = "Bob" }, person, new() { FirstName = "Carol" } };

        // Act
        var result = SubjectLookup.FindSubjectInCollection(list, 1);

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenValueIsArray_ThenReturnsSubjectAtIndex()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        Person[] array = [new() { FirstName = "Bob" }, person];

        // Act
        var result = SubjectLookup.FindSubjectInCollection(array, 1);

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenValueIsNonListEnumerable_ThenReturnsSubjectAtIndex()
    {
        // Arrange
        var person = new Person { FirstName = "Target" };
        var items = ImmutableQueue.Create<Person>(
            new Person { FirstName = "First" },
            person,
            new Person { FirstName = "Third" });

        // Act
        var result = SubjectLookup.FindSubjectInCollection(items, 1);

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenListElementIsNotSubject_ThenReturnsNull()
    {
        // Arrange
        var list = new List<object> { "hello", 42 };

        // Act
        var result = SubjectLookup.FindSubjectInCollection(list, 0);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenIndexExceedsEnumerableCount_ThenReturnsNull()
    {
        // Arrange
        var items = ImmutableQueue.Create<Person>(new Person { FirstName = "Only" });

        // Act
        var result = SubjectLookup.FindSubjectInCollection(items, 5);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenValueIsNotEnumerable_ThenReturnsNull()
    {
        // Arrange & Act
        var result = SubjectLookup.FindSubjectInCollection(42, 0);

        // Assert
        Assert.Null(result);
    }

    // --- FindSubjectInDictionary ---

    [Fact]
    public void WhenValueIsDictionary_ThenReturnsSubjectAtKey()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var dict = new Dictionary<string, Person> { ["key1"] = person };

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dict, "key1");

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenDictionaryKeyMissing_ThenReturnsNull()
    {
        // Arrange
        var dict = new Dictionary<string, Person> { ["key1"] = new() { FirstName = "Alice" } };

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dict, "nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenValueIsReadOnlyDictionary_ThenReturnsSubjectViaKvpFallback()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var wrapper = new ReadOnlyDictionaryWrapper<string, Person>(
            new Dictionary<string, Person> { ["found"] = person });

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(wrapper, "found");

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenReadOnlyDictionaryKeyMissing_ThenReturnsNull()
    {
        // Arrange
        var wrapper = new ReadOnlyDictionaryWrapper<string, Person>(
            new Dictionary<string, Person> { ["exists"] = new() { FirstName = "A" } });

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(wrapper, "missing");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenDictionaryValueIsNotSubject_ThenReturnsNull()
    {
        // Arrange
        var dict = new Dictionary<string, string> { ["key"] = "not a subject" };

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dict, "key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenValueIsNotDictionaryOrEnumerable_ThenReturnsNull()
    {
        // Arrange & Act
        var result = SubjectLookup.FindSubjectInDictionary(42, "key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenItemIsKvpWithSubjectValue_ThenReturnsTrueWithKeyAndSubject()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        object item = new KeyValuePair<string, Person>("myKey", person);

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.True(result);
        Assert.Equal("myKey", key);
        Assert.Same(person, subject);
    }

    [Fact]
    public void WhenItemIsKvpWithNonSubjectValue_ThenReturnsFalse()
    {
        // Arrange
        object item = new KeyValuePair<string, int>("myKey", 42);

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.False(result);
        Assert.Null(key);
        Assert.Null(subject);
    }

    [Fact]
    public void WhenItemIsKvpWithNullValue_ThenReturnsFalse()
    {
        // Arrange
        object item = new KeyValuePair<string, Person?>("myKey", null);

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.False(result);
        Assert.Null(key);
        Assert.Null(subject);
    }

    [Fact]
    public void WhenItemIsNotKvp_ThenReturnsFalse()
    {
        // Arrange
        object item = "just a string";

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.False(result);
        Assert.Null(key);
        Assert.Null(subject);
    }

    [Fact]
    public void WhenItemIsIntegerKvpWithSubjectValue_ThenReturnsTrueWithIntKey()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        object item = new KeyValuePair<int, Person>(7, person);

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.True(result);
        Assert.Equal(7, key);
        Assert.Same(person, subject);
    }

    [Fact]
    public void WhenCalledRepeatedlyWithSameType_ThenCacheIsUsed()
    {
        // Arrange
        var person1 = new Person { FirstName = "A" };
        var person2 = new Person { FirstName = "B" };
        object item1 = new KeyValuePair<string, Person>("k1", person1);
        object item2 = new KeyValuePair<string, Person>("k2", person2);

        // Act
        SubjectLookup.TryGetSubjectFromKeyValuePair(item1, out var key1, out var subject1);
        SubjectLookup.TryGetSubjectFromKeyValuePair(item2, out var key2, out var subject2);

        // Assert
        Assert.Equal("k1", key1);
        Assert.Same(person1, subject1);
        Assert.Equal("k2", key2);
        Assert.Same(person2, subject2);
    }

    private sealed class ReadOnlyDictionaryWrapper<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _inner;

        public ReadOnlyDictionaryWrapper(Dictionary<TKey, TValue> inner) => _inner = inner;

        public TValue this[TKey key] => _inner[key];
        public IEnumerable<TKey> Keys => _inner.Keys;
        public IEnumerable<TValue> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
