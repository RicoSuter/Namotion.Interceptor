using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class InheritancePathTests
{
    [Fact]
    public void WhenTryGetPath_InheritedProperty_ReturnsPropertyName()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher(context) { FirstName = "Alice" };

        // Act — FirstName is inherited from Person
        var path = teacher
            .TryGetRegisteredProperty(t => t.FirstName)?
            .TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("FirstName", path);
    }

    [Fact]
    public void WhenTryGetPath_OwnProperty_ReturnsPropertyName()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher(context) { MainCourse = "Math" };

        // Act — MainCourse is declared on Teacher
        var path = teacher
            .TryGetRegisteredProperty(t => t.MainCourse)?
            .TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("MainCourse", path);
    }

    [Fact]
    public void WhenTryGetPath_NestedInheritedProperty_ReturnsFullPath()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher { FirstName = "Alice", MainCourse = "Math" };
        var person = new Person(context)
        {
            FirstName = "Root",
            Mother = teacher
        };

        // Act — Mother is a Teacher, FirstName is inherited from Person
        var path = teacher
            .TryGetRegisteredProperty(t => t.FirstName)?
            .TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Mother.FirstName", path);
    }

    [Fact]
    public void WhenTryGetPath_NestedOwnProperty_ReturnsFullPath()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher { FirstName = "Alice", MainCourse = "Math" };
        var person = new Person(context)
        {
            FirstName = "Root",
            Mother = teacher
        };

        // Act — MainCourse is Teacher's own property
        var path = teacher
            .TryGetRegisteredProperty(t => t.MainCourse)?
            .TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Mother.MainCourse", path);
    }

    [Fact]
    public void WhenTryGetPath_InheritedPropertyWithRootSubject_ReturnsRelativePath()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher { FirstName = "Alice", MainCourse = "Math" };
        var person = new Person(context)
        {
            FirstName = "Root",
            Mother = teacher
        };

        // Act — get path relative to teacher itself
        var path = teacher
            .TryGetRegisteredProperty(t => t.FirstName)?
            .TryGetPath(DefaultPathProvider.Instance, teacher);

        // Assert
        Assert.Equal("FirstName", path);
    }

    [Fact]
    public void WhenGetAllProperties_OnInheritedSubject_IncludesBothBaseAndOwnProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher(context) { FirstName = "Alice", MainCourse = "Math" };

        // Act
        var allPaths = teacher
            .TryGetRegisteredSubject()?
            .GetAllProperties()
            .GetPaths(DefaultPathProvider.Instance, teacher)
            .Select(p => p.path)
            .Order()
            .ToArray() ?? [];

        // Assert
        Assert.Contains("FirstName", allPaths);
        Assert.Contains("MainCourse", allPaths);
    }

    [Fact]
    public void WhenUsingDerivedAttribute_ThenPropertiesIncludeBaseAndOwnProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var light = new DimmableLight(context) { Name = "Desk Lamp", On = true, Brightness = 0.8 };

        // Act
        var registeredSubject = light.TryGetRegisteredSubject();
        var propertyNames = registeredSubject?.Properties
            .Select(p => p.Name)
            .Order()
            .ToArray() ?? [];

        // Assert — must include both base (Name, On) and own (Brightness) properties
        Assert.Equal(["Brightness", "Name", "On"], propertyNames);
    }

    [Fact]
    public void WhenUsingDerivedAttribute_ThenInheritedPropertiesAreAccessible()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var light = new DimmableLight(context) { Name = "Desk Lamp", On = true, Brightness = 0.8 };

        // Act & Assert — base class properties should be resolvable
        Assert.Equal("Desk Lamp", light.TryGetRegisteredProperty("Name")?.GetValue());
        Assert.Equal(true, light.TryGetRegisteredProperty("On")?.GetValue());
        Assert.Equal(0.8, light.TryGetRegisteredProperty("Brightness")?.GetValue());
    }

    [Fact]
    public void WhenTeacherInCollection_ThenPathIncludesIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var teacher = new Teacher { FirstName = "Alice", MainCourse = "Math" };
        var person = new Person(context)
        {
            FirstName = "Root",
            Children = [teacher]
        };

        // Act — inherited property through collection
        var inheritedPath = teacher
            .TryGetRegisteredProperty(t => t.FirstName)?
            .TryGetPath(DefaultPathProvider.Instance, null);

        var ownPath = teacher
            .TryGetRegisteredProperty(t => t.MainCourse)?
            .TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Children[0].FirstName", inheritedPath);
        Assert.Equal("Children[0].MainCourse", ownPath);
    }
}
