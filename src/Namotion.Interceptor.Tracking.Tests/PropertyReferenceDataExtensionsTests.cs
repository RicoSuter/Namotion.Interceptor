using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class PropertyReferenceDataExtensionsTests
{
    [Fact]
    public void WhenGetOrAddCalledFirstTime_ThenCreatesNewValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = property.GetOrAddPropertyData("test-key", () => new List<string> { "initial" });

        // Assert
        Assert.Single(result);
        Assert.Equal("initial", result[0]);
    }

    [Fact]
    public void WhenGetOrAddCalledTwice_ThenReturnsSameInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var first = property.GetOrAddPropertyData("test-key", () => new List<string>());
        first.Add("modified");
        var second = property.GetOrAddPropertyData("test-key", () => new List<string>());

        // Assert
        Assert.Same(first, second);
        Assert.Single(second);
        Assert.Equal("modified", second[0]);
    }

    [Fact]
    public void WhenGetOrAddUsedWithDifferentKeys_ThenCreatesIndependentValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var data1 = property.GetOrAddPropertyData("key1", () => "value1");
        var data2 = property.GetOrAddPropertyData("key2", () => "value2");

        // Assert
        Assert.Equal("value1", data1);
        Assert.Equal("value2", data2);
    }

    [Fact]
    public void WhenAddOrUpdateCalledFirstTime_ThenCreatesAndUpdates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = property.AddOrUpdatePropertyData<List<string>, string>(
            "test-key",
            (list, value) => list.Add(value),
            "first");

        // Assert
        Assert.Single(result);
        Assert.Equal("first", result[0]);
    }

    [Fact]
    public void WhenAddOrUpdateCalledMultipleTimes_ThenUpdatesExistingInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var first = property.AddOrUpdatePropertyData<List<string>, string>(
            "test-key",
            (list, value) => list.Add(value),
            "first");

        var second = property.AddOrUpdatePropertyData<List<string>, string>(
            "test-key",
            (list, value) => list.Add(value),
            "second");

        // Assert
        Assert.Same(first, second);
        Assert.Equal(2, second.Count);
        Assert.Equal("first", second[0]);
        Assert.Equal("second", second[1]);
    }

    [Fact]
    public void WhenDifferentPropertiesUseGetOrAdd_ThenDataIsIsolated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();
        var person = new Person(context);
        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));

        // Act
        var data1 = firstNameProp.GetOrAddPropertyData("test-key", () => "first-name-data");
        var data2 = lastNameProp.GetOrAddPropertyData("test-key", () => "last-name-data");

        // Assert
        Assert.Equal("first-name-data", data1);
        Assert.Equal("last-name-data", data2);
    }
}
