using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class IncludedPathTests
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
    public void WhenEveryPropertyOnThePathIsIncluded_ThenItHasAnIncludedPath()
    {
        // Arrange
        var (root, property) = CreateChain();
        var pathProvider = new ExcludingPathProvider(nameof(Person.LastName));

        // Act
        var hasIncludedPath = property.HasIncludedPath(pathProvider, root);

        // Assert
        Assert.True(hasIncludedPath);
        Assert.Equal("Father.Mother.FirstName", property.TryGetPath(pathProvider, root));
    }

    [Theory]
    [InlineData(nameof(Person.Father))]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.FirstName))]
    public void WhenAPropertyOnThePathIsExcluded_ThenItHasNoIncludedPath(string excludedPropertyName)
    {
        // Arrange
        var (root, property) = CreateChain();
        var pathProvider = new ExcludingPathProvider(excludedPropertyName);

        // Act
        var hasIncludedPath = property.HasIncludedPath(pathProvider, root);

        // Assert
        Assert.False(hasIncludedPath);
        Assert.Null(property.TryGetPath(pathProvider, root));
    }

    [Fact]
    public void WhenTheInlinePathsDictionaryOnThePathIsExcluded_ThenItHasNoIncludedPath()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestInlineContainer(context) { Name = "Root" };
        var child = new TestInlineContainer(context) { Name = "Child" };
        root.Children = new Dictionary<string, TestInlineContainer> { ["a"] = child };
        var property = GetProperty(child, nameof(TestInlineContainer.Name));

        // Act
        var hasIncludedPath = property.HasIncludedPath(new ExcludingPathProvider(nameof(TestInlineContainer.Children)), root);
        var hasIncludedPathWhenIncluded = property.HasIncludedPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.False(hasIncludedPath);
        Assert.True(hasIncludedPathWhenIncluded);
        Assert.Equal("a.Name", property.TryGetPath(DefaultPathProvider.Instance, root));
    }

    [Fact]
    public void WhenAnotherParentReachesTheRootThroughIncludedProperties_ThenItHasAnIncludedPath()
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
        var hasIncludedPath = property.HasIncludedPath(pathProvider, root);

        // Assert
        Assert.True(hasIncludedPath);
        Assert.Equal("Mother.FirstName", property.TryGetPath(pathProvider, root));
    }

    [Fact]
    public void WhenTheRootIsNotReachable_ThenItHasNoIncludedPath()
    {
        // Arrange
        var (_, property) = CreateChain();
        var otherRoot = new Person(CreateContext()) { FirstName = "Other" };

        // Act
        var hasIncludedPath = property.HasIncludedPath(DefaultPathProvider.Instance, otherRoot);

        // Assert
        Assert.False(hasIncludedPath);
    }

    [Fact]
    public void WhenTheParentChainHasACycle_ThenItHasNoIncludedPath()
    {
        // Arrange
        var context = CreateContext();
        var a = new Person(context) { FirstName = "A" };
        var b = new Person(context) { FirstName = "B" };
        a.Father = b;
        b.Father = a;
        var property = GetProperty(a, nameof(Person.FirstName));

        // Act
        var hasIncludedPath = property.HasIncludedPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.False(hasIncludedPath);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(nameof(Person.Father), null)]
    [InlineData(nameof(Person.Mother), null)]
    [InlineData(nameof(Person.Children), null)]
    [InlineData(nameof(Person.FirstName), null)]
    [InlineData(nameof(Person.FirstName_MaxLength), null)]
    [InlineData(null, "root")]
    [InlineData(nameof(Person.Father), "root")]
    [InlineData(nameof(Person.Mother), "root")]
    [InlineData(nameof(Person.Children), "root")]
    [InlineData(nameof(Person.FirstName), "root")]
    [InlineData(nameof(Person.FirstName_MaxLength), "root")]
    public void WhenAPathIsBuiltWithTheSameProviderAndRoot_ThenItExistsExactlyWhenThereIsAnIncludedPath(string? excludedPropertyName, string? root)
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
        var rootSubject = root is null ? null : rootPerson;
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
                property.TryGetPath(pathProvider, rootSubject) is not null,
                property.HasIncludedPath(pathProvider, rootSubject));
        }
    }

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != excludedPropertyName;
    }
}
