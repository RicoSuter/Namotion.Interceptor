using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Connectors.Tests.Paths;

public class InheritancePathTests
{
    [Fact]
    public void WhenTryGetPropertyFromPath_InheritedPropertyOnDerivedSubject_ResolvesCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var employee = new Employee(context) { FirstName = "Alice", Department = "Science" };

        // Act — resolve inherited property by path
        var (property, _) = employee
            .TryGetPropertyFromPath("FirstName", DefaultPathProvider.Instance);

        // Assert
        Assert.NotNull(property);
        Assert.Equal("FirstName", property.Name);
    }

    [Fact]
    public void WhenTryGetPropertyFromPath_OwnPropertyOnDerivedSubject_ResolvesCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var employee = new Employee(context) { FirstName = "Alice", Department = "Science" };

        // Act — resolve own property by path
        var (property, _) = employee
            .TryGetPropertyFromPath("Department", DefaultPathProvider.Instance);

        // Assert
        Assert.NotNull(property);
        Assert.Equal("Department", property.Name);
    }

    [Fact]
    public void WhenTryGetPropertyFromPath_InheritedPropertyInCollection_ResolvesCorrectly()
    {
        // Arrange — matches reported scenario: Collection[0].InheritedProp where [0] is a derived type
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var employee = new Employee { FirstName = "Alice", Department = "Science" };
        var person = new Person(context)
        {
            FirstName = "Root",
            Children = [employee]
        };

        // Act — resolve inherited property through collection index
        var (property, _) = person
            .TryGetPropertyFromPath("Children[0].FirstName", DefaultPathProvider.Instance);

        // Assert
        Assert.NotNull(property);
        Assert.Equal("FirstName", property.Name);
        Assert.Equal(employee, property.Parent.Subject);
    }

    [Fact]
    public void WhenTryGetPropertyFromPath_OwnPropertyInCollection_ResolvesCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var employee = new Employee { FirstName = "Alice", Department = "Science" };
        var person = new Person(context)
        {
            FirstName = "Root",
            Children = [employee]
        };

        // Act — resolve derived-only property through collection index
        var (property, _) = person
            .TryGetPropertyFromPath("Children[0].Department", DefaultPathProvider.Instance);

        // Assert
        Assert.NotNull(property);
        Assert.Equal("Department", property.Name);
        Assert.Equal(employee, property.Parent.Subject);
    }

    [Fact]
    public void WhenRoundTrip_InheritedPropertyInCollection_ResolvesBackToSameProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var employee = new Employee { FirstName = "Alice", Department = "Science" };
        var person = new Person(context)
        {
            FirstName = "Root",
            Children = [employee]
        };

        // Act — round-trip: get path then resolve back
        var originalProperty = employee.TryGetRegisteredProperty(e => e.FirstName)!;
        var path = originalProperty.TryGetPath(DefaultPathProvider.Instance, null)!;
        var (resolvedProperty, _) = person
            .TryGetPropertyFromPath(path, DefaultPathProvider.Instance);

        // Assert
        Assert.Equal("Children[0].FirstName", path);
        Assert.NotNull(resolvedProperty);
        Assert.Equal(originalProperty.Name, resolvedProperty.Name);
        Assert.Equal(originalProperty.Parent.Subject, resolvedProperty.Parent.Subject);
    }

    [Fact]
    public void WhenRoundTrip_OwnPropertyInCollection_ResolvesBackToSameProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var employee = new Employee { FirstName = "Alice", Department = "Science" };
        var person = new Person(context)
        {
            FirstName = "Root",
            Children = [employee]
        };

        // Act — round-trip for derived-only property
        var originalProperty = employee.TryGetRegisteredProperty(e => e.Department)!;
        var path = originalProperty.TryGetPath(DefaultPathProvider.Instance, null)!;
        var (resolvedProperty, _) = person
            .TryGetPropertyFromPath(path, DefaultPathProvider.Instance);

        // Assert
        Assert.Equal("Children[0].Department", path);
        Assert.NotNull(resolvedProperty);
        Assert.Equal(originalProperty.Name, resolvedProperty.Name);
        Assert.Equal(originalProperty.Parent.Subject, resolvedProperty.Parent.Subject);
    }
}
