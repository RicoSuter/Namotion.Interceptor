using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathProviderInclusionTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    [Fact]
    public void WhenHoldingPropertyIsExcluded_ThenNoPathIsBuiltThroughIt()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context)
        {
            Father = new Person(context) { FirstName = "Dad", Mother = new Person(context) { FirstName = "Grandma" } },
            Mother = new Person(context) { FirstName = "Mom" }
        };
        var pathProvider = new ExcludingPathProvider(nameof(Person.Father));

        // Act
        var throughExcluded = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!.TryGetPath(pathProvider, null);
        var deepThroughExcluded = person.TryGetRegisteredProperty(p => p.Father!.Mother!.FirstName)!.TryGetPath(pathProvider, person);
        var excludedItself = person.TryGetRegisteredProperty(p => p.Father)!.TryGetPath(pathProvider, null);
        var included = person.TryGetRegisteredProperty(p => p.Mother!.FirstName)!.TryGetPath(pathProvider, null);
        var belowExcludedFromItsSubject = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!.TryGetPath(pathProvider, person.Father);

        // Assert
        Assert.Null(throughExcluded);
        Assert.Null(deepThroughExcluded);
        Assert.Null(excludedItself);
        Assert.Equal("Mother.FirstName", included);
        Assert.Equal("FirstName", belowExcludedFromItsSubject);
    }

    [Fact]
    public void WhenSharedSubjectIsHeldThroughAnExcludedAndAnIncludedProperty_ThenPathGoesThroughTheIncludedOne()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person(context) { FirstName = "Shared" };
        var person = new Person(context) { Father = shared, Children = [shared] };
        var pathProvider = new ExcludingPathProvider(nameof(Person.Father));

        // Act
        var path = shared.TryGetRegisteredProperty(p => p.FirstName)!.TryGetPath(pathProvider, person);

        // Assert
        Assert.Equal("Children[0].FirstName", path);
    }

    [Theory]
    [InlineData(null, "Mother.Father.FirstName")]
    [InlineData(nameof(Person.Mother), "Children[0].Father.FirstName")]
    [InlineData(nameof(Person.Father), null)]
    public void WhenSubjectHasSeveralParents_ThenPathIsTheFirstInParentOrderThroughIncludedProperties(
        string? excludedPropertyName, string? expectedPath)
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person { FirstName = "Shared" };
        var root = new Person(context)
        {
            Mother = new Person { Father = shared },
            Children = [new Person { Father = shared }]
        };
        var pathProvider = new ExcludingPathProvider(excludedPropertyName ?? "");

        // Act
        var path = shared.TryGetRegisteredProperty(p => p.FirstName)!.TryGetPath(pathProvider, root);

        // Assert
        Assert.Equal(2, shared.TryGetRegisteredSubject()!.Parents.Length);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void WhenHoldingPropertyIsExcluded_ThenNoPathResolvesThroughIt()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.Father));
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var property = pathProvider.TryGetPropertyFromPath(registered, "Father.FirstName");
        var holdingProperty = pathProvider.TryGetPropertyFromPath(registered, "Father");
        var subject = pathProvider.TryGetSubjectFromPath(registered, "Father");
        var properties = pathProvider.GetPropertiesFromPaths(registered, ["Father.FirstName", "FirstName"]).ToList();

        // Assert
        Assert.Null(property);
        Assert.Null(holdingProperty);
        Assert.Null(subject);
        Assert.Equal("FirstName", Assert.Single(properties).Property.Name);
    }

    [Fact]
    public void WhenLeafIsExcluded_ThenItIsNeitherBuiltNorResolved()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { LastName = "Doe" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.LastName));

        // Act
        var path = person.TryGetRegisteredProperty(p => p.Father!.LastName)!.TryGetPath(pathProvider, null);
        var property = pathProvider.TryGetPropertyFromPath(person.TryGetRegisteredSubject()!, "Father.LastName");

        // Assert
        Assert.Null(path);
        Assert.Null(property);
    }

    [Fact]
    public void WhenPropertyIsExcluded_ThenItsAttributesAreNeitherBuiltNorResolved()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.FirstName));
        var firstName = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!;
        var attribute = firstName.TryGetAttribute("MaxLength")!;
        var attributeOfAttribute = attribute.TryGetAttribute("Unit")!;
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var attributePath = attribute.TryGetPath(pathProvider, person);
        var attributeOfAttributePath = attributeOfAttribute.TryGetPath(pathProvider, person);
        var resolvedAttribute = pathProvider.TryGetPropertyFromPath(registered, "Father.MaxLength");
        var resolvedAttributeOfAttribute = pathProvider.TryGetPropertyFromPath(registered, "Father.Unit");
        var paths = new[] { attribute, attributeOfAttribute }.GetPaths(pathProvider, person).ToList();

        // Assert
        Assert.Null(attributePath);
        Assert.Null(attributeOfAttributePath);
        Assert.Null(resolvedAttribute);
        Assert.Null(resolvedAttributeOfAttribute);
        Assert.Empty(paths);
    }

    [Fact]
    public void WhenAttributeIsExcluded_ThenItsAttributeIsNeitherBuiltNorResolvedButItsPropertyIs()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.FirstName_MaxLength));
        var firstName = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!;
        var attributeOfAttribute = firstName.TryGetAttribute("MaxLength")!.TryGetAttribute("Unit")!;
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var propertyPath = firstName.TryGetPath(pathProvider, person);
        var attributeOfAttributePath = attributeOfAttribute.TryGetPath(pathProvider, person);
        var resolvedAttributeOfAttribute = pathProvider.TryGetPropertyFromPath(registered, "Father.Unit");

        // Assert
        Assert.Equal("Father.FirstName", propertyPath);
        Assert.Null(attributeOfAttributePath);
        Assert.Null(resolvedAttributeOfAttribute);
    }

    [Fact]
    public void WhenPropertyAndItsAttributesAreIncluded_ThenAttributePathsAreBuiltAndResolved()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.LastName));
        var attribute = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!.TryGetAttribute("MaxLength")!;
        var attributeOfAttribute = attribute.TryGetAttribute("Unit")!;
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var attributePath = attribute.TryGetPath(pathProvider, person);
        var attributeOfAttributePath = attributeOfAttribute.TryGetPath(pathProvider, person);
        var resolvedAttribute = pathProvider.TryGetPropertyFromPath(registered, attributePath!);
        var resolvedAttributeOfAttribute = pathProvider.TryGetPropertyFromPath(registered, attributeOfAttributePath!);

        // Assert
        Assert.Equal("Father.MaxLength", attributePath);
        Assert.Equal("Father.Unit", attributeOfAttributePath);
        Assert.Same(attribute, resolvedAttribute?.Property);
        Assert.Same(attributeOfAttribute, resolvedAttributeOfAttribute?.Property);
    }

    [Fact]
    public void WhenAttributeHoldsSubjectBelowExcludedProperty_ThenNoPathIsBuiltOrResolvedThroughIt()
    {
        // Arrange
        var context = CreateContext();
        var child = new AttributeHolder(context) { Label = "Child" };
        var root = new AttributeHolder(context) { Label = "Root", Label_Child = child };
        var pathProvider = new ExcludingPathProvider(nameof(AttributeHolder.Label));
        var value = child.TryGetRegisteredProperty(c => c.Value)!;
        var registered = root.TryGetRegisteredSubject()!;

        // Act
        var pathWithRoot = value.TryGetPath(pathProvider, root);
        var pathWithoutRoot = value.TryGetPath(pathProvider, null);
        var resolvedProperty = pathProvider.TryGetPropertyFromPath(registered, "Child.Value");
        var resolvedSubject = pathProvider.TryGetSubjectFromPath(registered, "Child");

        // Assert
        Assert.Equal("Child.Value", value.TryGetPath(DefaultPathProvider.Instance, root));
        Assert.Null(pathWithRoot);
        Assert.Null(pathWithoutRoot);
        Assert.Null(resolvedProperty);
        Assert.Null(resolvedSubject);
    }

    [Fact]
    public void WhenSharedSubjectIsHeldByAttributeOfExcludedPropertyAndByIncludedProperty_ThenPathGoesThroughTheIncludedOne()
    {
        // Arrange
        var context = CreateContext();
        var shared = new AttributeHolder(context) { Label = "Shared" };
        var root = new AttributeHolder(context) { Label = "Root", Label_Child = shared };
        root.Other = shared;
        var pathProvider = new ExcludingPathProvider(nameof(AttributeHolder.Label));

        // Act
        var path = shared.TryGetRegisteredProperty(c => c.Value)!.TryGetPath(pathProvider, root);

        // Assert
        Assert.Equal(nameof(AttributeHolder.Label_Child), shared.TryGetRegisteredSubject()!.Parents[0].Property.Name);
        Assert.Equal("Other.Value", path);
    }

    [Fact]
    public void WhenInlinePathsDictionaryIsExcluded_ThenItsKeysAreNeitherBuiltNorResolved()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Server" };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { ["Servers"] = child }
        };
        var pathProvider = new ExcludingPathProvider(nameof(TestInlineContainer.Children));
        var registered = root.TryGetRegisteredSubject()!;

        // Act
        var path = child.TryGetRegisteredProperty(c => c.Name)!.TryGetPath(pathProvider, root);
        var bareKey = pathProvider.TryGetPropertyFromPath(registered, "Servers.Name");
        var explicitKey = pathProvider.TryGetPropertyFromPath(registered, "Children[Servers].Name");
        var subject = pathProvider.TryGetSubjectFromPath(registered, "Servers");

        // Assert
        Assert.Null(path);
        Assert.Null(bareKey);
        Assert.Null(explicitKey);
        Assert.Null(subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(nameof(Person.Father))]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.Children))]
    [InlineData(nameof(Person.FirstName))]
    [InlineData(nameof(Person.FirstName_MaxLength))]
    public void WhenPathIsBuiltWithAProvider_ThenItResolvesBackToTheSamePropertyAndPassesNoExcludedProperty(string? excludedPropertyName)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context)
        {
            FirstName = "Root",
            Father = new Person(context) { FirstName = "Dad", Mother = new Person(context) { FirstName = "Grandma" } },
            Mother = new Person(context) { FirstName = "Mom" },
            Children = [new Person(context) { FirstName = "Kid", Father = new Person(context) { FirstName = "Step" } }]
        };
        var pathProvider = new ExcludingPathProvider(excludedPropertyName ?? "");
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var results = registered
            .GetAllProperties()
            .Select(property => (Property: property, Path: property.TryGetPath(pathProvider, person)))
            .ToList();

        // Assert
        Assert.Contains(results, result => result.Path is not null);
        foreach (var (property, path) in results)
        {
            Assert.Equal(IsChainIncluded(property, person, pathProvider), path is not null);
            if (path is not null)
            {
                var resolved = pathProvider.TryGetPropertyFromPath(registered, path);
                Assert.NotNull(resolved);
                Assert.Same(property, resolved.Value.Property);
            }
        }
    }

    [Fact]
    public void WhenASubjectOnTheChainMovesBehindAnExcludedPropertyWhileItIsChecked_ThenNoPathIsBuiltThroughIt()
    {
        // Arrange
        // On its first check of father.Father the provider moves the father behind the excluded Mother, which the walk
        // then reaches.
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var father = new Person(context) { FirstName = "Father" };
        var grand = new Person(context) { FirstName = "Grand" };
        root.Father = father;
        father.Father = grand;
        var property = grand.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;
        var pathProvider = new MovingPathProvider(father.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Father))!, () =>
        {
            root.Mother = father;
            root.Father = null;
        });

        // Act
        var path = property.TryGetPath(pathProvider, root, null);

        // Assert
        Assert.True(pathProvider.IsMoved);
        Assert.Null(path);
    }

    private static bool IsChainIncluded(RegisteredSubjectProperty property, IInterceptorSubject root, PathProviderBase pathProvider)
    {
        while (true)
        {
            if (!pathProvider.IsPropertyIncluded(property) ||
                (property.IsAttribute && !IsPropertyOfAttributeIncluded(property, pathProvider)))
            {
                return false;
            }

            if (property.Subject == root)
            {
                return true;
            }

            property = property.Parent.Parents[0].Property;
        }
    }

    private static bool IsPropertyOfAttributeIncluded(RegisteredSubjectProperty attribute, PathProviderBase pathProvider)
    {
        var property = attribute.GetAttributedProperty();
        return pathProvider.IsPropertyIncluded(property) &&
            (!property.IsAttribute || IsPropertyOfAttributeIncluded(property, pathProvider));
    }

    private sealed class MovingPathProvider(RegisteredSubjectProperty trigger, Action move) : PathProviderBase
    {
        public bool IsMoved { get; private set; }

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (!IsMoved && property == trigger)
            {
                IsMoved = true;
                move();
            }

            segment = property.Name != nameof(Person.Mother) ? property.BrowseName : null;
            return segment is not null;
        }
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class AttributeHolder
{
    public partial string? Label { get; set; }

    [Namotion.Interceptor.Registry.Attributes.PropertyAttribute(nameof(Label), "Child")]
    public partial AttributeHolder? Label_Child { get; set; }

    public partial AttributeHolder? Other { get; set; }

    public partial string? Value { get; set; }
}
