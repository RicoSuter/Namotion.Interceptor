using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class HasPathTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    private static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    // root.Father.Mother.FirstName
    private static (Person Root, RegisteredSubjectProperty Property) CreateChain()
    {
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var father = new Person(context) { FirstName = "Father" };
        var mother = new Person(context) { FirstName = "Mother" };
        root.Father = father;
        father.Mother = mother;
        return (root, GetProperty(mother, nameof(Person.FirstName)));
    }

    [Fact]
    public void WhenEveryPropertyOnThePathIsIncluded_ThenItHasAPath()
    {
        // Arrange
        var (root, property) = CreateChain();
        var pathProvider = new ExcludingPathProvider(nameof(Person.LastName));

        // Act
        var hasPath = property.HasPath(pathProvider, root);

        // Assert
        Assert.True(hasPath);
        Assert.Equal("Father.Mother.FirstName", property.TryGetPath(pathProvider, root));
    }

    [Theory]
    [InlineData(nameof(Person.Father))]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.FirstName))]
    public void WhenAPropertyOnThePathIsExcluded_ThenItHasNoPath(string excludedPropertyName)
    {
        // Arrange
        var (root, property) = CreateChain();
        var pathProvider = new ExcludingPathProvider(excludedPropertyName);

        // Act
        var hasPath = property.HasPath(pathProvider, root);

        // Assert
        Assert.False(hasPath);
        Assert.Null(property.TryGetPath(pathProvider, root));
    }

    [Fact]
    public void WhenTheInlinePathsDictionaryOnThePathIsExcluded_ThenItHasNoPath()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestInlineContainer(context) { Name = "Root" };
        var child = new TestInlineContainer(context) { Name = "Child" };
        root.Children = new Dictionary<string, TestInlineContainer> { ["a"] = child };
        var property = GetProperty(child, nameof(TestInlineContainer.Name));

        // Act
        var hasPath = property.HasPath(new ExcludingPathProvider(nameof(TestInlineContainer.Children)), root);
        var hasPathWhenIncluded = property.HasPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.False(hasPath);
        Assert.True(hasPathWhenIncluded);
        Assert.Equal("a.Name", property.TryGetPath(DefaultPathProvider.Instance, root));
    }

    [Fact]
    public void WhenAnotherParentReachesTheRootThroughIncludedProperties_ThenItHasAPath()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        var shared = new Person(context) { FirstName = "Shared" };
        root.Father = shared;
        root.Mother = shared;
        var property = GetProperty(shared, nameof(Person.FirstName));
        var pathProvider = new ExcludingPathProvider(nameof(Person.Father));

        // Act
        var hasPath = property.HasPath(pathProvider, root);

        // Assert
        Assert.True(hasPath);
        Assert.Equal("Mother.FirstName", property.TryGetPath(pathProvider, root));
    }

    [Fact]
    public void WhenTheRootIsNotReachable_ThenItHasNoPath()
    {
        // Arrange
        var (_, property) = CreateChain();
        var otherRoot = new Person(CreateContext()) { FirstName = "Other" };

        // Act
        var hasPath = property.HasPath(DefaultPathProvider.Instance, otherRoot);

        // Assert
        Assert.False(hasPath);
    }

    [Fact]
    public void WhenTheParentChainHasACycle_ThenItHasNoPath()
    {
        // Arrange
        var context = CreateContext();
        var a = new Person(context) { FirstName = "A" };
        var b = new Person(context) { FirstName = "B" };
        a.Father = b;
        b.Father = a;
        var root = new Person(context) { FirstName = "Root" };
        var property = GetProperty(a, nameof(Person.FirstName));

        // Act
        var hasPath = property.HasPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.False(hasPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(nameof(Person.Father))]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.Children))]
    [InlineData(nameof(Person.FirstName))]
    [InlineData(nameof(Person.FirstName_MaxLength))]
    public void WhenAPathIsBuiltWithTheSameProviderAndRoot_ThenItExistsExactlyWhenItHasAPath(string? excludedPropertyName)
    {
        // Arrange
        // The shared subject is held through Father.Mother and Mother.Children, so a root search has two parents to
        // choose from, and the outside subject holds one that does not reach the root.
        var context = CreateContext();
        var rootPerson = new Person(context) { FirstName = "Root" };
        var father = new Person(context) { FirstName = "Father" };
        var mother = new Person(context) { FirstName = "Mother" };
        var shared = new Person(context) { FirstName = "Shared" };
        var outside = new Person(context) { FirstName = "Outside" };
        rootPerson.Father = father;
        rootPerson.Mother = mother;
        father.Mother = shared;
        mother.Children = [new Person(context) { FirstName = "Child" }, shared];
        outside.Father = new Person(context) { FirstName = "OutsideFather" };
        var pathProvider = new ExcludingPathProvider(excludedPropertyName ?? "");
        var properties = new List<RegisteredSubjectProperty>();
        foreach (var subject in new IInterceptorSubject[] { rootPerson, father, mother, shared, outside, outside.Father })
        {
            properties.AddRange(subject.TryGetRegisteredSubject()!.Properties);
        }

        // Act & Assert
        foreach (var property in properties)
        {
            Assert.Equal(
                property.TryGetPath(pathProvider, rootPerson) is not null,
                property.HasPath(pathProvider, rootPerson));
        }
    }
}
