using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathPropertiesTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    private static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    // Writes the properties as the default path provider writes a path without [InlinePaths] keys.
    private static string Format(List<(RegisteredSubjectProperty Property, object? Index)> properties) =>
        string.Join('.', properties.Select(entry =>
            entry.Index is null ? entry.Property.BrowseName : $"{entry.Property.BrowseName}[{PathExtensions.FormatPathIndex(entry.Index)}]"));

    private static List<(RegisteredSubjectProperty Property, object? Index)> AssertMatchesWrittenPath(
        RegisteredSubjectProperty property, IInterceptorSubject root, object? propertyIndex = null)
    {
        var properties = new List<(RegisteredSubjectProperty Property, object? Index)>();
        Assert.True(property.TryGetPathProperties(root, propertyIndex, properties));
        Assert.Equal(property.TryGetPath(DefaultPathProvider.Instance, root, propertyIndex), Format(properties));
        return properties;
    }

    [Fact]
    public void WhenEverySubjectHasOneParent_ThenThePropertiesRunFromTheRootDownToTheProperty()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var father = new Person(context) { FirstName = "Father" };
        var mother = new Person(context) { FirstName = "Mother" };
        root.Father = father;
        father.Mother = mother;

        // Act
        var properties = AssertMatchesWrittenPath(GetProperty(mother, nameof(Person.FirstName)), root);

        // Assert
        Assert.Equal(
            [(GetProperty(root, nameof(Person.Father)), null), (GetProperty(father, nameof(Person.Mother)), null), (GetProperty(mother, nameof(Person.FirstName)), (object?)null)],
            properties);
    }

    [Fact]
    public void WhenTheFirstParentDoesNotReachTheRoot_ThenThePropertiesGoThroughTheParentThatDoes()
    {
        // Arrange
        var context = CreateContext();
        var outside = new Person(context) { FirstName = "Outside" };
        var root = new Person(context) { FirstName = "Root" };
        var shared = new Person(context) { FirstName = "Shared" };
        outside.Father = shared;
        root.Mother = shared;

        // Act
        var properties = AssertMatchesWrittenPath(GetProperty(shared, nameof(Person.FirstName)), root);

        // Assert
        Assert.Equal("Mother.FirstName", Format(properties));
        Assert.Same(GetProperty(root, nameof(Person.Mother)), properties[0].Property);
    }

    [Fact]
    public void WhenTheRootIsNotReachable_ThenItReturnsFalseAndLeavesTheListEmpty()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var other = new Person(context) { FirstName = "Other" };
        other.Father = new Person(context) { FirstName = "Father" };
        var property = GetProperty(other.Father, nameof(Person.FirstName));
        var properties = new List<(RegisteredSubjectProperty Property, object? Index)> { (property, null) };

        // Act
        var result = property.TryGetPathProperties(root, null, properties);

        // Assert
        Assert.False(result);
        Assert.Empty(properties);
        Assert.Null(property.TryGetPath(DefaultPathProvider.Instance, root));
    }

    [Fact]
    public void WhenASubjectIsInADictionary_ThenItsPropertyCarriesTheKey()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var item = new TestItem(context) { Value = "Item" };
        container.Items = new Dictionary<string, TestItem> { ["a"] = item };

        // Act
        var properties = AssertMatchesWrittenPath(GetProperty(item, nameof(TestItem.Value)), container);

        // Assert
        Assert.Equal([(GetProperty(container, nameof(TestContainer.Items)), "a"), (GetProperty(item, nameof(TestItem.Value)), (object?)null)], properties);
    }

    [Fact]
    public void WhenASubjectIsInACollection_ThenItsPropertyCarriesThePosition()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var second = new Person(context) { FirstName = "Second" };
        root.Children = [new Person(context) { FirstName = "First" }, second];

        // Act
        var properties = AssertMatchesWrittenPath(GetProperty(second, nameof(Person.FirstName)), root);

        // Assert
        Assert.Equal([(GetProperty(root, nameof(Person.Children)), 1), (GetProperty(second, nameof(Person.FirstName)), (object?)null)], properties);
    }

    [Fact]
    public void WhenAPropertyIndexIsGiven_ThenTheLastPropertyCarriesIt()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var father = new Person(context) { FirstName = "Father" };
        root.Father = father;
        father.Children = [new Person(context) { FirstName = "Child" }];

        // Act
        var properties = AssertMatchesWrittenPath(GetProperty(father, nameof(Person.Children)), root, propertyIndex: 0);

        // Assert
        Assert.Equal([(GetProperty(root, nameof(Person.Father)), null), (GetProperty(father, nameof(Person.Children)), (object?)0)], properties);
    }

    [Fact]
    public void WhenThePathIsWrittenWithAList_ThenTheListHoldsTheChainItIsWrittenThrough()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var second = new Person(context) { FirstName = "Second" };
        root.Children = [new Person(context) { FirstName = "First" }, second];
        var properties = new List<(RegisteredSubjectProperty Property, object? Index)> { (GetProperty(root, nameof(Person.FirstName)), null) };

        // Act
        var path = GetProperty(second, nameof(Person.FirstName)).TryGetPath(DefaultPathProvider.Instance, root, null, properties);

        // Assert
        Assert.Equal("Children[1].FirstName", path);
        Assert.Equal([(GetProperty(root, nameof(Person.Children)), 1), (GetProperty(second, nameof(Person.FirstName)), (object?)null)], properties);
    }

    [Fact]
    public void WhenNoPathIsWrittenWithAList_ThenTheListIsLeftEmpty()
    {
        // Arrange
        var context = CreateContext();
        var outside = new Person(context) { FirstName = "Outside" };
        var root = new Person(context) { FirstName = "Root" };
        var properties = new List<(RegisteredSubjectProperty Property, object? Index)> { (GetProperty(root, nameof(Person.FirstName)), null) };

        // Act
        var path = GetProperty(outside, nameof(Person.FirstName)).TryGetPath(DefaultPathProvider.Instance, root, null, properties);

        // Assert
        Assert.Null(path);
        Assert.Empty(properties);
    }
}
