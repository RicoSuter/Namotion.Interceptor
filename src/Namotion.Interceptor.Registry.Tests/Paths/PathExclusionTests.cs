using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathExclusionTests
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
    public void WhenEveryPropertyOnThePathIsIncluded_ThenNoneIsExcluded()
    {
        // Arrange
        var (root, property) = CreateChain();
        var pathProvider = new ExcludingPathProvider(nameof(Person.LastName));

        // Act
        var isExcluded = property.IsAnyPropertyOnPathExcluded(pathProvider, root, null);

        // Assert
        Assert.False(isExcluded);
        Assert.Equal("Father.Mother.FirstName", property.TryGetPath(pathProvider, root));
    }

    [Theory]
    [InlineData(nameof(Person.Father))]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.FirstName))]
    public void WhenAPropertyOnThePathIsExcluded_ThenOneIsExcluded(string excludedPropertyName)
    {
        // Arrange
        var (root, property) = CreateChain();
        var pathProvider = new ExcludingPathProvider(excludedPropertyName);

        // Act
        var isExcluded = property.IsAnyPropertyOnPathExcluded(pathProvider, root, null);

        // Assert
        Assert.True(isExcluded);
        Assert.Null(property.TryGetPath(pathProvider, root));
    }

    [Fact]
    public void WhenTheInlinePathsDictionaryOnThePathIsExcluded_ThenOneIsExcluded()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestInlineContainer(context) { Name = "Root" };
        var child = new TestInlineContainer(context) { Name = "Child" };
        root.Children = new Dictionary<string, TestInlineContainer> { ["a"] = child };
        var property = GetProperty(child, nameof(TestInlineContainer.Name));

        // Act
        var isExcluded = property.IsAnyPropertyOnPathExcluded(new ExcludingPathProvider(nameof(TestInlineContainer.Children)), root, null);
        var isExcludedWhenIncluded = property.IsAnyPropertyOnPathExcluded(DefaultPathProvider.Instance, root, null);

        // Assert
        Assert.True(isExcluded);
        Assert.False(isExcludedWhenIncluded);
        Assert.Equal("a.Name", property.TryGetPath(DefaultPathProvider.Instance, root));
    }

    [Fact]
    public void WhenAPropertyIndexIsGiven_ThenThePropertyHoldingItIsChecked()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "Root" };
        root.Children = [new Person(context) { FirstName = "Child" }];
        var property = GetProperty(root, nameof(Person.Children));

        // Act
        var isExcluded = property.IsAnyPropertyOnPathExcluded(new ExcludingPathProvider(nameof(Person.Children)), root, 0);
        var isExcludedWhenIncluded = property.IsAnyPropertyOnPathExcluded(DefaultPathProvider.Instance, root, 0);

        // Assert
        Assert.True(isExcluded);
        Assert.False(isExcludedWhenIncluded);
    }

    [Fact]
    public void WhenAnotherParentReachesTheRootThroughIncludedProperties_ThenNoneIsExcluded()
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
        var isExcluded = property.IsAnyPropertyOnPathExcluded(pathProvider, root, null);

        // Assert
        Assert.False(isExcluded);
        Assert.Equal("Mother.FirstName", property.TryGetPath(pathProvider, root));
    }

    [Fact]
    public void WhenTheRootIsNotReachable_ThenNoneIsExcluded()
    {
        // Arrange
        var (_, property) = CreateChain();
        var otherRoot = new Person(CreateContext()) { FirstName = "Other" };

        // Act
        var isExcluded = property.IsAnyPropertyOnPathExcluded(new ExcludingPathProvider(nameof(Person.Father)), otherRoot, null);

        // Assert
        Assert.False(isExcluded);
    }

    [Fact]
    public void WhenTheParentChainHasACycle_ThenNoneIsExcluded()
    {
        // Arrange
        var context = CreateContext();
        var a = new Person(context) { FirstName = "A" };
        var b = new Person(context) { FirstName = "B" };
        a.Father = b;
        b.Father = a;
        var property = GetProperty(a, nameof(Person.FirstName));

        // Act
        var isExcluded = property.IsAnyPropertyOnPathExcluded(new ExcludingPathProvider(nameof(Person.Father)), null, null);

        // Assert
        Assert.False(isExcluded);
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
    public void WhenAPathExists_ThenAPropertyIsExcludedExactlyWhenNoPathIsBuilt(string? excludedPropertyName, string? root)
    {
        // Arrange
        // The shared subject is held through Father and Children, so a root search has two parents to choose from.
        var context = CreateContext();
        var rootPerson = new Person(context) { FirstName = "Root" };
        var father = new Person(context) { FirstName = "Father" };
        var mother = new Person(context) { FirstName = "Mother" };
        var shared = new Person(context) { FirstName = "Shared" };
        rootPerson.Father = father;
        rootPerson.Mother = mother;
        father.Mother = shared;
        mother.Children = [new Person(context) { FirstName = "Child" }, shared];
        var rootSubject = root is null ? null : rootPerson;
        var pathProvider = new ExcludingPathProvider(excludedPropertyName ?? "");
        var properties = new List<RegisteredSubjectProperty>();
        foreach (var subject in new IInterceptorSubject[] { rootPerson, father, mother, shared })
        {
            properties.AddRange(subject.TryGetRegisteredSubject()!.Properties);
        }

        // Act & Assert
        foreach (var property in properties.Where(property => property.TryGetPath(DefaultPathProvider.Instance, rootSubject) is not null))
        {
            Assert.Equal(
                property.TryGetPath(pathProvider, rootSubject) is null,
                property.IsAnyPropertyOnPathExcluded(pathProvider, rootSubject, null));
        }
    }

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != excludedPropertyName;
    }
}
