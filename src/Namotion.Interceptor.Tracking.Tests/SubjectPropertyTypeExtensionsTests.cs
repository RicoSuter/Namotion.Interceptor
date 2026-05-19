using System.Collections;
using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class SubjectPropertyTypeExtensionsTests
{
    [Fact]
    public void WhenPlainSubjectType_ThenIsReferenceTypeOnly()
    {
        // Arrange
        var type = typeof(Person);

        // Act
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // Assert
        Assert.True(isReference);
        Assert.False(isCollection);
        Assert.False(isDictionary);
    }

    [Fact]
    public void WhenListOfSubjects_ThenIsCollectionTypeOnly()
    {
        // Arrange
        var type = typeof(List<Person>);

        // Act
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // Assert
        Assert.False(isReference);
        Assert.True(isCollection);
        Assert.False(isDictionary);
    }

    [Fact]
    public void WhenDictionaryOfSubjects_ThenIsDictionaryTypeOnly()
    {
        // Arrange
        var type = typeof(Dictionary<string, Person>);

        // Act
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // Assert
        Assert.False(isReference);
        Assert.False(isCollection);
        Assert.True(isDictionary);
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

        // The element-mutual-exclusivity check rejects T-as-element because T is itself
        // a container of candidates, so the outer collection check fails. With no
        // dictionary or collection classification, the type lands on Reference.
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

        // The value-mutual-exclusivity check rejects T-as-value (T is itself a dict-of-
        // candidates), so the outer dictionary check fails. With no collection or
        // dictionary classification, the type lands on Reference.
        Assert.True(isReference);
        Assert.False(isCollection);
        Assert.False(isDictionary);
    }

    [Fact]
    public void WhenListElementIsItselfACollectionOfSubjects_ThenOuterIsNotClassifiedAsCollection()
    {
        // Arrange: a list whose element type is itself a collection of subjects
        // (e.g. List<IEnumerable<Person>>) is NOT classified as a subject collection.
        // Container-of-container is not how the loader represents collections of subjects,
        // so the outer property should not match.
        var type = typeof(List<IEnumerable<Person>>);

        // Act
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // Assert
        Assert.False(isReference);
        Assert.False(isCollection);
        Assert.False(isDictionary);
    }

    [Fact]
    public void WhenPropertyTypeIsBareIEnumerableOfSubjects_ThenClassifiedAsCollection()
    {
        // Arrange: declaring a property as IEnumerable<Person> (rather than List<Person>
        // or Person[]) should still classify as a collection. GetInterfaces() does not
        // return the type itself, so the classifier explicitly includes the bare
        // IEnumerable<> in its enumerable view to make this case work.
        var type = typeof(IEnumerable<Person>);

        // Act
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // Assert
        Assert.True(isCollection);
        Assert.False(isReference);
        Assert.False(isDictionary);
    }

    [Fact]
    public void WhenPropertyTypeIsBareIEnumerableOfKeyValuePairOfSubjects_ThenClassifiedAsDictionary()
    {
        // Arrange: same idea as the collection case but for dictionaries. Declaring a
        // property as IEnumerable<KeyValuePair<string, Person>> (rather than
        // Dictionary<string, Person> or IDictionary<string, Person>) should still classify
        // as a dictionary.
        var type = typeof(IEnumerable<KeyValuePair<string, Person>>);

        // Act
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // Assert
        Assert.True(isDictionary);
        Assert.False(isCollection);
        Assert.False(isReference);
    }

    [Fact]
    public void WhenPropertyTypeIsGenericInterfaceOverNonSubjects_ThenNotClassifiedAsReference()
    {
        // Arrange: generic interfaces whose type arguments cannot structurally hold a
        // subject (IList<int>, IEnumerable<int>, IDictionary<string, int>) must not be
        // classified as subject references at the property level. Mirrors how they are
        // rejected as element types; otherwise downstream subject-tracking code would
        // try to assign subjects to properties that can never hold them.
        var listOfInts = typeof(IList<int>);
        var enumerableOfInts = typeof(IEnumerable<int>);
        var dictionaryOfInts = typeof(IDictionary<string, int>);

        // Act & Assert
        Assert.False(listOfInts.IsSubjectReferenceType());
        Assert.False(listOfInts.IsSubjectCollectionType());
        Assert.False(listOfInts.IsSubjectDictionaryType());

        Assert.False(enumerableOfInts.IsSubjectReferenceType());
        Assert.False(enumerableOfInts.IsSubjectCollectionType());
        Assert.False(enumerableOfInts.IsSubjectDictionaryType());

        Assert.False(dictionaryOfInts.IsSubjectReferenceType());
        Assert.False(dictionaryOfInts.IsSubjectCollectionType());
        Assert.False(dictionaryOfInts.IsSubjectDictionaryType());
    }

    [Fact]
    public void WhenPropertyTypeIsNonEnumerableInterface_ThenStillClassifiedAsReference()
    {
        // Arrange: regression guard. Plain interfaces that are not enumerable (and not
        // IInterceptorSubject themselves) must still classify as subject references,
        // since they could legitimately hold a subject implementing them via polymorphism.
        var comparable = typeof(IComparable);
        var formattable = typeof(IFormattable);

        // Act & Assert
        Assert.True(comparable.IsSubjectReferenceType());
        Assert.True(formattable.IsSubjectReferenceType());
    }

    [Fact]
    public void WhenListElementIsAGenericContainerOfNonSubjects_ThenOuterIsNotClassifiedAsCollection()
    {
        // Arrange: List<IList<int>> previously misclassified as a collection because
        // IList<int> qualified as an interface candidate and the element check fell
        // through to "single reference". The element holds non-subjects, so the outer
        // must not be treated as a collection-of-subjects.
        var type = typeof(List<IList<int>>);

        // Act
        var isReference = type.IsSubjectReferenceType();
        var isCollection = type.IsSubjectCollectionType();
        var isDictionary = type.IsSubjectDictionaryType();

        // Assert
        Assert.False(isReference);
        Assert.False(isCollection);
        Assert.False(isDictionary);
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
}
