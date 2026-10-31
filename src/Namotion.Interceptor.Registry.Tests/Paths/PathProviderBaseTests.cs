using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathProviderBaseTests
{
    public static TheoryData<string> ProviderNames =>
    [
        "Default", "DerivedDefault", "Attribute", "DerivedAttribute", "Excluding"
    ];

    [Theory]
    [MemberData(nameof(ProviderNames))]
    public void WhenInclusionAndSegmentAreAskedInOneCall_ThenTheyAgreeWithIsPropertyIncludedAndTryGetPropertySegment(string providerName)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann", LastName = "Smith", Father = new Person(context) };
        var container = new TestShadowedInlineContainer(context) { Label = "Label", Name = "Name" };
        var pathProvider = CreateProvider(providerName);
        var properties = person.TryGetRegisteredSubject()!.GetAllProperties()
            .Concat(container.TryGetRegisteredSubject()!.GetAllProperties())
            .ToList();

        // Act
        var results = properties
            .Select(property => (Property: property, Included: pathProvider.TryGetIncludedPropertySegment(property, out var segment), Segment: segment))
            .ToList();

        // Assert
        Assert.Contains(results, result => result.Included);
        foreach (var (property, included, segment) in results)
        {
            var expectedIncluded = pathProvider.IsPropertyIncluded(property);
            Assert.Equal(expectedIncluded, included);
            Assert.Equal(expectedIncluded ? pathProvider.TryGetPropertySegment(property) ?? property.BrowseName : null, segment);
        }
    }

    [Fact]
    public void WhenDerivedAttributeProviderExcludesAnAnnotatedHolder_ThenNoPathIsBuiltOrResolvedThroughIt()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestMappedInlineContainer(context) { Name = "Child" };
        var root = new TestMappedInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestMappedInlineContainer> { ["child"] = child }
        };
        var pathProvider = new ExcludingAttributePathProvider(nameof(TestMappedInlineContainer.Children));
        var name = child.TryGetRegisteredProperty(c => c.Name)!;

        // Act
        var path = name.TryGetPath(pathProvider, root);
        var resolved = pathProvider.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, "child.name");

        // Assert
        Assert.Equal("child.name", name.TryGetPath(new AttributeBasedPathProvider("test"), root));
        Assert.Null(path);
        Assert.Null(resolved);
    }

    [Theory]
    [InlineData(nameof(Person.FirstName), false, false, false)]
    [InlineData(nameof(Person.FirstName_MaxLength), true, false, false)]
    [InlineData(nameof(Person.FirstName_MaxLength_Unit), true, true, false)]
    [InlineData(nameof(Person.LastName), true, true, true)]
    public void WhenAPropertyIsExcluded_ThenPathsPassNeitherItNorTheAttributesBelongingToIt(
        string excludedPropertyName, bool firstNameIncluded, bool maxLengthIncluded, bool unitIncluded)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann" };
        var pathProvider = new ExcludingPathProvider(excludedPropertyName);
        var firstName = person.TryGetRegisteredProperty(p => p.FirstName)!;
        var maxLength = firstName.TryGetAttribute("MaxLength")!;
        var unit = maxLength.TryGetAttribute("Unit")!;

        // Act
        var firstNameResult = pathProvider.IsPropertyIncludedInPaths(firstName);
        var maxLengthResult = pathProvider.IsPropertyIncludedInPaths(maxLength);
        var unitResult = pathProvider.IsPropertyIncludedInPaths(unit);

        // Assert
        Assert.Equal(firstNameIncluded, firstNameResult);
        Assert.Equal(maxLengthIncluded, maxLengthResult);
        Assert.Equal(unitIncluded, unitResult);
        Assert.Equal(unitIncluded, unit.TryGetPath(pathProvider, person) is not null);
    }

    [Fact]
    public void WhenAttributesBelongToEachOtherInACycle_ThenPathsPassNeitherOfThem()
    {
        // Arrange
        var context = CreateContext();
        var subject = new CyclicAttributeHolder(context) { First = "a", Second = "b" };
        var registered = subject.TryGetRegisteredSubject()!;
        var first = registered.TryGetProperty(nameof(CyclicAttributeHolder.First))!;
        var second = registered.TryGetProperty(nameof(CyclicAttributeHolder.Second))!;
        var pathProvider = DefaultPathProvider.Instance;

        // Act
        var firstIncluded = pathProvider.IsPropertyIncludedInPaths(first);
        var secondIncluded = pathProvider.IsPropertyIncludedInPaths(second);
        var firstPath = first.TryGetPath(pathProvider, subject);
        var resolved = pathProvider.TryGetPropertyFromPath(registered, first.BrowseName);

        // Assert
        Assert.True(first.IsAttribute);
        Assert.True(second.IsAttribute);
        Assert.False(firstIncluded);
        Assert.False(secondIncluded);
        Assert.Null(firstPath);
        Assert.Null(resolved);
    }

    private static PathProviderBase CreateProvider(string name) => name switch
    {
        "Default" => DefaultPathProvider.Instance,
        "DerivedDefault" => new UpperCasePathProvider(),
        "Attribute" => new AttributeBasedPathProvider("test"),
        "DerivedAttribute" => new ExcludingAttributePathProvider(nameof(TestShadowedInlineContainer.Name)),
        "Excluding" => new ExcludingPathProvider(nameof(Person.FirstName)),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    private sealed class UpperCasePathProvider() : DefaultPathProvider('.')
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != nameof(Person.LastName);

        public override string? TryGetPropertySegment(RegisteredSubjectProperty property) => property.BrowseName.ToUpperInvariant();
    }

    private sealed class ExcludingAttributePathProvider(string excludedPropertyName) : AttributeBasedPathProvider("test")
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
            => property.Name != excludedPropertyName && base.IsPropertyIncluded(property);
    }

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != excludedPropertyName;
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class CyclicAttributeHolder
{
    [Namotion.Interceptor.Registry.Attributes.PropertyAttribute(nameof(Second), "First")]
    public partial string? First { get; set; }

    [Namotion.Interceptor.Registry.Attributes.PropertyAttribute(nameof(First), "Second")]
    public partial string? Second { get; set; }
}
