using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class SubjectPropertyTypeExtensionsTests
{
    [Theory]
    // --- Single subject references ---
    [InlineData(typeof(IInterceptorSubject),                                true,  false, false)]
    [InlineData(typeof(object),                                             true,  false, false)]
    [InlineData(typeof(Person),                                             true,  false, false)]
    [InlineData(typeof(IComparable),                                        true,  false, false)]
    [InlineData(typeof(IFormattable),                                       true,  false, false)]
    // IInterceptorSubject always wins, even when the class also implements a container interface.
    [InlineData(typeof(HybridSubjectGenericDictionary),                     true,  false, false)]
    [InlineData(typeof(HybridSubjectNonGenericDictionary),                  true,  false, false)]
    [InlineData(typeof(HybridSubjectGenericList),                           true,  false, false)]
    // Self-referential subject containers also stay references (covered in dedicated regression facts below).

    // --- Subject collections ---
    [InlineData(typeof(object[]),                                           false, true,  false)]
    [InlineData(typeof(List<object>),                                       false, true,  false)]
    [InlineData(typeof(List<IInterceptorSubject>),                          false, true,  false)]
    [InlineData(typeof(Person[]),                                           false, true,  false)]
    [InlineData(typeof(List<Person>),                                       false, true,  false)]
    [InlineData(typeof(IEnumerable<Person>),                                false, true,  false)]
    [InlineData(typeof(ImmutableArray<Person>),                             false, true,  false)]
    [InlineData(typeof(ImmutableArray<object>),                             false, true,  false)]
    [InlineData(typeof(ImmutableArray<IComparable>),                        false, true,  false)]
    [InlineData(typeof(ArrayList),                                          false, true,  false)]

    // --- Subject dictionaries ---
    [InlineData(typeof(Dictionary<string, Person>),                         false, false, true)]
    [InlineData(typeof(IDictionary<string, Person>),                        false, false, true)]
    [InlineData(typeof(IEnumerable<KeyValuePair<string, Person>>),          false, false, true)]
    [InlineData(typeof(Hashtable),                                          false, false, true)]

    // --- Not subject-carrying ---
    [InlineData(typeof(int),                                                false, false, false)]
    [InlineData(typeof(decimal),                                            false, false, false)]
    [InlineData(typeof(string),                                             false, false, false)]
    [InlineData(typeof(IList<int>),                                         false, false, false)]
    [InlineData(typeof(IEnumerable<int>),                                   false, false, false)]
    [InlineData(typeof(IDictionary<string, int>),                          false, false, false)]
    [InlineData(typeof(ImmutableArray<int>),                                false, false, false)]
    // Element-of-container-of-non-subjects: outer is not a subject collection.
    [InlineData(typeof(List<IEnumerable<Person>>),                          false, false, false)]
    [InlineData(typeof(List<IList<int>>),                                   false, false, false)]
    // List<concrete non-subject class> is statically rejected. A subclass could theoretically
    // add IInterceptorSubject, but the library uses declared types for classification by design
    // (use List<object> or List<ISomeBase> if polymorphic subject content is intended).
    [InlineData(typeof(List<NonSubjectPlainClass>),                         false, false, false)]
    public void WhenType_ThenClassifiedAccordingToTable(Type type, bool isReference, bool isCollection, bool isDictionary)
    {
        // Act
        var actualReference = type.IsSubjectReferenceType();
        var actualCollection = type.IsSubjectCollectionType();
        var actualDictionary = type.IsSubjectDictionaryType();
        var canContain = type.CanContainSubjects();

        // Assert: exact classification.
        Assert.Equal(isReference, actualReference);
        Assert.Equal(isCollection, actualCollection);
        Assert.Equal(isDictionary, actualDictionary);

        // Assert: mutual exclusivity (at most one true) and CanContainSubjects is the OR.
        var trueCount = (actualReference ? 1 : 0) + (actualCollection ? 1 : 0) + (actualDictionary ? 1 : 0);
        Assert.True(trueCount <= 1, $"{type.Name}: more than one classifier returned true (ref={actualReference}, col={actualCollection}, dict={actualDictionary}).");
        Assert.Equal(actualReference || actualCollection || actualDictionary, canContain);
    }

    [Fact]
    public void WhenTypeImplementsIInterceptorSubjectAndIEnumerableOfSelf_ThenClassificationTerminates()
    {
        // Arrange: pathological self-referential shape that would stack-overflow under a
        // naive recursive classification (IsSubjectReferenceType -> IsSubjectCollectionType
        // -> element check -> IsSubjectReferenceType -> ...). Regression for the structural
        // fix that breaks the recursion at the element-type check.
        var type = typeof(SelfReferentialSubjectCollection);

        // Act & Assert: must not throw / stack-overflow.
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // IInterceptorSubject wins, so this lands on Reference regardless of the
        // enumerable shape.
        Assert.True(isReference);
        Assert.False(isCollection);
        Assert.False(isDictionary);
    }

    [Fact]
    public void WhenTypeImplementsIInterceptorSubjectAndIEnumerableOfKeyValuePairOfSelf_ThenClassificationTerminates()
    {
        // Arrange: pathological self-referential dictionary shape. Same recursion-risk
        // class as the collection regression, exercising the IsSubjectDictionaryTypeSlow
        // path instead. Without the structural fix, the value-type check inside
        // IsSubjectDictionaryTypeSlow would recurse through IsSubjectReferenceType.
        var type = typeof(SelfReferentialSubjectDictionary);

        // Act & Assert: must not throw / stack-overflow.
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // IInterceptorSubject wins, so this lands on Reference regardless of the
        // dictionary-shaped enumerable.
        Assert.True(isReference);
        Assert.False(isCollection);
        Assert.False(isDictionary);
    }

    private sealed class NonSubjectPlainClass
    {
    }

    private sealed class SelfReferentialSubjectCollection : IInterceptorSubject, IEnumerable<SelfReferentialSubjectCollection>
    {
        public object SyncRoot => throw new NotSupportedException();
        public IInterceptorSubjectContext Context => throw new NotSupportedException();
        public ConcurrentDictionary<(string? property, string key), object?> Data => throw new NotSupportedException();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => throw new NotSupportedException();
        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();

        public IEnumerator<SelfReferentialSubjectCollection> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SelfReferentialSubjectDictionary : IInterceptorSubject, IEnumerable<KeyValuePair<string, SelfReferentialSubjectDictionary>>
    {
        public object SyncRoot => throw new NotSupportedException();
        public IInterceptorSubjectContext Context => throw new NotSupportedException();
        public ConcurrentDictionary<(string? property, string key), object?> Data => throw new NotSupportedException();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => throw new NotSupportedException();
        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();

        public IEnumerator<KeyValuePair<string, SelfReferentialSubjectDictionary>> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Hybrid: class declares itself a subject AND implements a generic dictionary of subjects.
    // Per IInterceptorSubject-wins rule, this must classify as Reference, not Dictionary.
    private sealed class HybridSubjectGenericDictionary : IInterceptorSubject, IDictionary<string, Person>
    {
        public object SyncRoot => throw new NotSupportedException();
        public IInterceptorSubjectContext Context => throw new NotSupportedException();
        public ConcurrentDictionary<(string? property, string key), object?> Data => throw new NotSupportedException();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => throw new NotSupportedException();
        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();

        public Person this[string key] { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public ICollection<string> Keys => throw new NotSupportedException();
        public ICollection<Person> Values => throw new NotSupportedException();
        public int Count => throw new NotSupportedException();
        public bool IsReadOnly => throw new NotSupportedException();
        public void Add(string key, Person value) => throw new NotSupportedException();
        public void Add(KeyValuePair<string, Person> item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(KeyValuePair<string, Person> item) => throw new NotSupportedException();
        public bool ContainsKey(string key) => throw new NotSupportedException();
        public void CopyTo(KeyValuePair<string, Person>[] array, int arrayIndex) => throw new NotSupportedException();
        public IEnumerator<KeyValuePair<string, Person>> GetEnumerator() => throw new NotSupportedException();
        public bool Remove(string key) => throw new NotSupportedException();
        public bool Remove(KeyValuePair<string, Person> item) => throw new NotSupportedException();
        public bool TryGetValue(string key, out Person value) => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Hybrid: subject + legacy non-generic IDictionary. Per IInterceptorSubject-wins rule,
    // must classify as Reference, not Dictionary via the non-generic fallback.
    private sealed class HybridSubjectNonGenericDictionary : IInterceptorSubject, IDictionary
    {
        public object SyncRoot => throw new NotSupportedException();
        public IInterceptorSubjectContext Context => throw new NotSupportedException();
        public ConcurrentDictionary<(string? property, string key), object?> Data => throw new NotSupportedException();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => throw new NotSupportedException();
        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();

        public object? this[object key] { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public bool IsFixedSize => throw new NotSupportedException();
        public bool IsReadOnly => throw new NotSupportedException();
        public ICollection Keys => throw new NotSupportedException();
        public ICollection Values => throw new NotSupportedException();
        public int Count => throw new NotSupportedException();
        public bool IsSynchronized => throw new NotSupportedException();
        public void Add(object key, object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object key) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
        public IDictionaryEnumerator GetEnumerator() => throw new NotSupportedException();
        public void Remove(object key) => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Hybrid: subject + generic list of subjects. Per IInterceptorSubject-wins rule,
    // must classify as Reference, not Collection.
    private sealed class HybridSubjectGenericList : IInterceptorSubject, IList<Person>
    {
        public object SyncRoot => throw new NotSupportedException();
        public IInterceptorSubjectContext Context => throw new NotSupportedException();
        public ConcurrentDictionary<(string? property, string key), object?> Data => throw new NotSupportedException();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => throw new NotSupportedException();
        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();

        public Person this[int index] { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public int Count => throw new NotSupportedException();
        public bool IsReadOnly => throw new NotSupportedException();
        public void Add(Person item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(Person item) => throw new NotSupportedException();
        public void CopyTo(Person[] array, int arrayIndex) => throw new NotSupportedException();
        public IEnumerator<Person> GetEnumerator() => throw new NotSupportedException();
        public int IndexOf(Person item) => throw new NotSupportedException();
        public void Insert(int index, Person item) => throw new NotSupportedException();
        public bool Remove(Person item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
