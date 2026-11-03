using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
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
    public void WhenTheProviderIsAskedForASegment_ThenItGivesOneExactlyForTheIncludedProperties(string providerName)
    {
        // Arrange
        var (properties, _) = CreateProperties();
        var pathProvider = CreateProvider(providerName);

        // Act
        var results = properties
            .Select(property => (Property: property, Included: pathProvider.TryGetPropertySegment(property, out var segment), Segment: segment))
            .ToList();

        // Assert
        Assert.Contains(results, result => result.Included);
        foreach (var (property, included, segment) in results)
        {
            Assert.Equal(included, segment is not null);
            Assert.Equal(included, pathProvider.IsPropertyIncluded(property));
        }
    }

    [Theory]
    [MemberData(nameof(ProviderNames))]
    public void WhenASegmentIsResolved_ThenTheResultIsTheFirstIncludedPropertyWithThatSegment(string providerName)
    {
        // Arrange
        var (properties, subjects) = CreateProperties();
        var pathProvider = CreateProvider(providerName);
        var names = properties
            .SelectMany(property => new[] { property.Name, property.BrowseName, property.BrowseName.ToUpperInvariant(), property.Name.ToLowerInvariant() })
            .Distinct()
            .ToList();

        foreach (var subject in subjects)
        {
            foreach (var name in names)
            {
                // Act
                var found = pathProvider.TryGetPropertyFromSegment(subject, name, out var property);

                // Assert
                var inlinePathsProperty = GetInlinePathsProperty(subject);
                var expected = subject.Properties.FirstOrDefault(candidate =>
                    candidate != inlinePathsProperty && pathProvider.TryGetPropertySegment(candidate, out var segment) && segment == name) ??
                    (inlinePathsProperty is not null && pathProvider.IsPropertyIncluded(inlinePathsProperty) ? inlinePathsProperty : null);
                Assert.Equal(found, property is not null);
                Assert.Same(expected, property);
                Assert.True(!found || pathProvider.IsPropertyIncluded(property!));
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProviderNames))]
    public void WhenTheSegmentOfAnIncludedPropertyIsResolved_ThenItRoundTripsToAPropertyWithTheSameSegment(string providerName)
    {
        // Arrange
        var (properties, _) = CreateProperties();
        var pathProvider = CreateProvider(providerName);

        foreach (var property in properties)
        {
            if (!pathProvider.TryGetPropertySegment(property, out var segment))
            {
                continue;
            }

            // Act
            var found = pathProvider.TryGetPropertyFromSegment(property.Parent, segment, out var resolved);

            // Assert
            Assert.True(found);
            Assert.True(pathProvider.TryGetPropertySegment(resolved!, out var resolvedSegment));
            Assert.Equal(segment, resolvedSegment);
        }
    }

    [Fact]
    public void WhenTheInlinePathsPropertyIsExcluded_ThenAnUnknownSegmentResolvesToNoProperty()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestMappedInlineContainer(context) { Name = "Root" };
        var registered = container.TryGetRegisteredSubject()!;
        var pathProvider = new ExcludingAttributePathProvider(nameof(TestMappedInlineContainer.Children));

        // Act
        var found = pathProvider.TryGetPropertyFromSegment(registered, "child", out var property);
        var foundWhenIncluded = new AttributeBasedPathProvider("test").TryGetPropertyFromSegment(registered, "child", out var included);

        // Assert
        Assert.False(found);
        Assert.Null(property);
        Assert.True(foundWhenIncluded);
        Assert.Equal(nameof(TestMappedInlineContainer.Children), included!.Name);
    }

    [Fact]
    public void WhenADerivedDefaultProviderOverridesTheSegment_ThenPathsAreBuiltAndResolvedWithIt()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann", LastName = "Smith", Father = new Person(context) { FirstName = "Bob" } };
        var registered = person.TryGetRegisteredSubject()!;
        var pathProvider = new UpperCasePathProvider();
        var fatherFirstName = person.Father.TryGetRegisteredProperty(p => p.FirstName)!;
        var lastName = person.TryGetRegisteredProperty(p => p.LastName)!;

        // Act
        var path = fatherFirstName.TryGetPath(pathProvider, person);
        var excludedPath = lastName.TryGetPath(pathProvider, person);
        var resolved = pathProvider.TryGetPropertyFromPath(registered, "FATHER.FIRSTNAME");
        var resolvedBrowseName = pathProvider.TryGetPropertyFromPath(registered, "Father.FirstName");

        // Assert
        Assert.Equal("FATHER.FIRSTNAME", path);
        Assert.Null(excludedPath);
        Assert.Same(fatherFirstName, resolved?.Property);
        Assert.Null(resolvedBrowseName);
    }

    [Fact]
    public void WhenADerivedAttributeProviderOverridesTheSegment_ThenPathsAreBuiltAndResolvedWithIt()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestMappedInlineContainer(context) { Name = "Child" };
        var root = new TestMappedInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestMappedInlineContainer> { ["child"] = child }
        };
        var pathProvider = new PrefixingAttributePathProvider();
        var name = child.TryGetRegisteredProperty(c => c.Name)!;

        // Act
        var path = name.TryGetPath(pathProvider, root);
        var resolved = pathProvider.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, "child.x-name");

        // Assert
        Assert.Equal("child.x-name", path);
        Assert.Same(name, resolved?.Property);
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
        var firstNameResult = pathProvider.IsPropertyEffectivelyIncluded(firstName);
        var maxLengthResult = pathProvider.IsPropertyEffectivelyIncluded(maxLength);
        var unitResult = pathProvider.IsPropertyEffectivelyIncluded(unit);

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
        var firstIncluded = pathProvider.IsPropertyEffectivelyIncluded(first);
        var secondIncluded = pathProvider.IsPropertyEffectivelyIncluded(second);
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

    private static (List<RegisteredSubjectProperty> Properties, List<RegisteredSubject> Subjects) CreateProperties()
    {
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann", LastName = "Smith", Father = new Person(context) };
        var container = new TestShadowedInlineContainer(context) { Label = "Label", Name = "Name" };
        var lateShadowedContainer = new TestLateShadowedInlineContainer(context) { Label = "Label", Name = "Name" };
        var subjects = new List<RegisteredSubject>
        {
            person.TryGetRegisteredSubject()!,
            person.Father.TryGetRegisteredSubject()!,
            container.TryGetRegisteredSubject()!,
            lateShadowedContainer.TryGetRegisteredSubject()!
        };
        return (subjects.SelectMany(subject => subject.Properties).ToList(), subjects);
    }

    private static RegisteredSubjectProperty? GetInlinePathsProperty(RegisteredSubject subject)
        => InlinePathsAttribute.GetInlinePathsPropertyName(subject.Subject.GetType()) is { } name ? subject.TryGetProperty(name) : null;

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
        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            segment = property.Name != nameof(Person.LastName) ? property.BrowseName.ToUpperInvariant() : null;
            return segment is not null;
        }
    }

    private sealed class ExcludingAttributePathProvider(string excludedPropertyName) : AttributeBasedPathProvider("test")
    {
        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (property.Name == excludedPropertyName)
            {
                segment = null;
                return false;
            }

            return base.TryGetPropertySegment(property, out segment);
        }
    }

    private sealed class PrefixingAttributePathProvider() : AttributeBasedPathProvider("test")
    {
        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (!base.TryGetPropertySegment(property, out var baseSegment))
            {
                segment = null;
                return false;
            }

            segment = InlinePathsAttribute.IsInlinePathsProperty(property.Subject.GetType(), property.Name) ? baseSegment : "x-" + baseSegment;
            return true;
        }
    }

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            segment = property.Name != excludedPropertyName ? property.BrowseName : null;
            return segment is not null;
        }
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
