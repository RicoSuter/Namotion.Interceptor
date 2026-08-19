using System.Collections;

namespace Namotion.Interceptor.Tests;

public class SubjectPropertyTypeClassifierTests
{
    public static IEnumerable<object[]> ClassificationCases =>
    [
        [typeof(int), false, false, false],
        [typeof(string), false, false, false],
        [typeof(Car), true, false, false],
        [typeof(object), true, false, false],
        [typeof(IComparable), true, false, false],
        [typeof(IEnumerable<Car>), false, true, false],
        [typeof(IReadOnlyList<Car>), false, true, false],
        [typeof(IDictionary<string, Car>), false, false, true],
        [typeof(IReadOnlyDictionary<string, Car>), false, false, true],
        [typeof(ArrayList), false, true, false],
        [typeof(Hashtable), false, false, true],
        [typeof(IEnumerable<KeyValuePair<string, Car>>), false, false, false],
        [typeof(List<IEnumerable<Car>>), false, false, false]
    ];

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void WhenClassifyingPropertyType_ThenCoreAndExpectedShapeAgree(
        Type type, bool isReference, bool isCollection, bool isDictionary)
    {
        // Act
        var actualReference = SubjectPropertyTypeClassifier.IsSubjectReferenceType(type);
        var actualCollection = SubjectPropertyTypeClassifier.IsSubjectCollectionType(type);
        var actualDictionary = SubjectPropertyTypeClassifier.IsSubjectDictionaryType(type);

        // Assert
        Assert.Equal(isReference, actualReference);
        Assert.Equal(isCollection, actualCollection);
        Assert.Equal(isDictionary, actualDictionary);
        Assert.Equal(isReference || isCollection || isDictionary,
            SubjectPropertyTypeClassifier.CanContainSubjects(type));
        Assert.InRange((actualReference ? 1 : 0) + (actualCollection ? 1 : 0) +
            (actualDictionary ? 1 : 0), 0, 1);
    }

    [Theory]
    [InlineData(typeof(int), false)]
    [InlineData(typeof(Car), true)]
    [InlineData(typeof(IReadOnlyList<Car>), true)]
    public void WhenCreatingMetadata_ThenCanContainSubjectsIsDerivedFromType(Type type, bool expected)
    {
        // Arrange
        var metadata = new SubjectPropertyMetadata(
            "Value",
            type,
            Array.Empty<Attribute>(),
            getValue: null,
            setValue: null,
            isIntercepted: false,
            isDynamic: false);

        // Act
        var canContainSubjects = metadata.CanContainSubjects;

        // Assert
        Assert.Equal(expected, canContainSubjects);
    }
}
