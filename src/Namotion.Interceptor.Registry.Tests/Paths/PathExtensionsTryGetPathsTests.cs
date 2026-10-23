using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathExtensionsTryGetPathsTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    private static RegisteredSubject Registered(IInterceptorSubject subject) => subject.TryGetRegisteredSubject()!;

    private static string? TryGetPathOfFirstParent(RegisteredSubject subject, PathProviderBase pathProvider)
    {
        var parent = subject.Parents[0];
        return parent.Property.TryGetPath(pathProvider, rootSubject: null, parent.Index);
    }

    [Fact]
    public void WhenSubjectHasASingleChainOfParents_ThenThePathEqualsTryGetPath()
    {
        // Arrange
        var context = CreateContext();
        var grandfather = new Person(context);
        _ = new Person(context) { Father = new Person(context) { Father = grandfather } };
        var paths = new List<string>();

        // Act
        var complete = Registered(grandfather).TryGetPaths(DefaultPathProvider.Instance, paths);

        // Assert
        Assert.True(complete);
        Assert.Equal(["Father.Father"], paths);
        Assert.Equal(TryGetPathOfFirstParent(Registered(grandfather), DefaultPathProvider.Instance), paths[0]);
    }

    [Fact]
    public void WhenSubjectHasTwoParents_ThenBothPathsAreWrittenInParentOrder()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person(context);
        var root = new Person(context) { Father = shared };
        root.Children = [shared];
        var paths = new List<string>();

        // Act
        var complete = Registered(shared).TryGetPaths(DefaultPathProvider.Instance, paths);

        // Assert
        Assert.True(complete);
        Assert.Equal(["Father", "Children[0]"], paths);
        Assert.Equal(TryGetPathOfFirstParent(Registered(shared), DefaultPathProvider.Instance), paths[0]);
    }

    [Fact]
    public void WhenAnAncestorAboveTheParentHasTwoParents_ThenAPathPerBranchIsWritten()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new Person(context);
        var middle = new Person(context) { Father = leaf };
        var root = new Person(context);
        root.Mother = middle;
        root.Children = [middle];
        var paths = new List<string>();

        // Act
        var complete = Registered(leaf).TryGetPaths(DefaultPathProvider.Instance, paths);

        // Assert
        Assert.True(complete);
        Assert.Equal(["Mother.Father", "Children[0].Father"], paths);
        Assert.Equal(TryGetPathOfFirstParent(Registered(leaf), DefaultPathProvider.Instance), paths[0]);
    }

    [Fact]
    public void WhenSubjectHasNoParents_ThenNoPathIsWrittenAndTheSetIsComplete()
    {
        // Arrange
        var root = new Person(CreateContext());
        var paths = new List<string>();

        // Act
        var complete = Registered(root).TryGetPaths(DefaultPathProvider.Instance, paths);

        // Assert
        Assert.True(complete);
        Assert.Empty(paths);
    }

    [Fact]
    public void WhenAPropertyAboveTheParentIsExcluded_ThenTheOtherPathsAreWrittenAndTheSetIsIncomplete()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new Person(context);
        var middle = new Person(context) { Father = leaf };
        var root = new Person(context);
        root.Mother = middle;
        root.Children = [middle];
        var paths = new List<string>();

        // Act
        var complete = Registered(leaf).TryGetPaths(new ExcludingPathProvider(nameof(Person.Mother)), paths);

        // Assert
        Assert.False(complete);
        Assert.Equal(["Children[0].Father"], paths);
    }

    [Fact]
    public void WhenParentsFormACycle_ThenOnlyTheFinitePathsAreWritten()
    {
        // Arrange
        var context = CreateContext();
        var first = new Person(context);
        var second = new Person(context) { Father = first };
        first.Father = second;
        _ = new Person(context) { Mother = first };
        var paths = new List<string>();

        // Act
        var complete = Registered(first).TryGetPaths(DefaultPathProvider.Instance, paths);

        // Assert
        Assert.True(complete);
        Assert.Equal(["Mother"], paths);
    }

    [Fact]
    public void WhenMoreChainsExistThanMaxPaths_ThenMaxPathsAreWrittenAndTheSetIsIncomplete()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person(context);
        var root = new Person(context) { Father = shared, Mother = shared };
        root.Children = [shared];
        var paths = new List<string>();

        // Act
        var complete = Registered(shared).TryGetPaths(DefaultPathProvider.Instance, paths, maxPaths: 2);

        // Assert
        Assert.False(complete);
        Assert.Equal(["Father", "Mother"], paths);
    }

    [Fact]
    public void WhenExactlyMaxPathsChainsExist_ThenAllAreWrittenAndTheSetIsComplete()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person(context);
        var root = new Person(context) { Father = shared, Mother = shared };
        root.Children = [shared];
        var paths = new List<string>();

        // Act
        var complete = Registered(shared).TryGetPaths(DefaultPathProvider.Instance, paths, maxPaths: 3);

        // Assert
        Assert.True(complete);
        Assert.Equal(["Father", "Mother", "Children[0]"], paths);
    }

    [Fact]
    public void WhenAnInlinePathsKeyIsShadowed_ThenTheOtherPathsAreWrittenAndTheSetIsIncomplete()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestShadowedInlineContainer(context) { Name = "child", Label = "label" };
        var middle = new TestShadowedInlineContainer(context)
        {
            Name = "middle",
            Label = "label",
            Children = new Dictionary<string, TestShadowedInlineContainer> { ["a.b"] = child }
        };
        _ = new TestShadowedInlineContainer(context)
        {
            Name = "root",
            Label = "label",
            Children = new Dictionary<string, TestShadowedInlineContainer> { ["CNC01"] = child, ["Middle"] = middle }
        };
        var paths = new List<string>();

        // Act
        var complete = Registered(child).TryGetPaths(pathProvider, paths);

        // Assert
        Assert.False(complete);
        Assert.Equal(["CNC01"], paths);
    }

    [Fact]
    public void WhenParentsFormADenseComponentWithoutRoot_ThenTheSearchStopsAtItsBudget()
    {
        // Arrange
        var context = CreateContext();
        var items = Enumerable.Range(0, 8).Select(_ => new TestItem(context) { Value = "v" }).ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            var current = items[i];
            items[i].Children = items
                .Where(item => item != current)
                .Select((item, index) => (item, index))
                .ToDictionary(entry => entry.index.ToString(), entry => entry.item);
        }

        var paths = new List<string>();

        // Act
        var complete = Registered(items[0]).TryGetPaths(DefaultPathProvider.Instance, paths);

        // Assert
        Assert.False(complete);
        Assert.Empty(paths);
    }

    [Fact]
    public void WhenMaxPathsIsZero_ThenTryGetPathsThrows()
    {
        // Arrange
        var root = new Person(CreateContext());

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Registered(root).TryGetPaths(DefaultPathProvider.Instance, new List<string>(), maxPaths: 0));
    }

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != excludedPropertyName;
    }
}
