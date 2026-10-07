using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver.ResolvePartially() method.
/// </summary>
public class SubjectPathResolverResolvePartiallyTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenPathFullyResolves_ThenReturnsSubjectWithStepsAndNoNextProperty()
    {
        // Arrange
        var grandchild = new TestContainer { Name = "Grandchild" };
        var child = new TestContainer { Name = "Child" };
        child.Children = new Dictionary<string, TestContainer> { ["a"] = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("/Child/Children/a", PathStyle.Route);

        // Assert
        Assert.Same(grandchild, result.Subject);
        Assert.Same(grandchild, result.DeepestSubject);
        Assert.Null(result.NextProperty);
        Assert.Collection(result.Steps,
            step =>
            {
                Assert.Equal(nameof(TestContainer.Child), step.Property.Name);
                Assert.Null(step.Index);
                Assert.Same(child, step.Subject);
            },
            step =>
            {
                Assert.Equal(nameof(TestContainer.Children), step.Property.Name);
                Assert.Equal("a", step.Index);
                Assert.Same(grandchild, step.Subject);
            });
    }

    [Fact]
    public void WhenCanonicalPathFullyResolves_ThenReturnsSameSubjectAsResolveSubject()
    {
        // Arrange
        var grandchild = new TestContainer { Name = "Grandchild" };
        var child = new TestContainer { Name = "Child" };
        child.Children = new Dictionary<string, TestContainer> { ["a"] = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("/Child/Children[a]", PathStyle.Canonical);

        // Assert
        Assert.Same(Resolver.ResolveSubject("/Child/Children[a]", PathStyle.Canonical), result.Subject);
        Assert.Same(grandchild, result.Subject);
    }

    [Fact]
    public void WhenSubjectReferenceIsNull_ThenReturnsHolderAndReferenceProperty()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("/Child/Children/a", PathStyle.Route);

        // Assert
        Assert.Null(result.Subject);
        Assert.Same(root, result.DeepestSubject);
        Assert.Equal(new PropertyReference(root, nameof(TestContainer.Child)), result.NextProperty);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void WhenDictionaryKeyIsMissing_ThenReturnsDeepestSubjectAndDictionaryProperty()
    {
        // Arrange
        var child = new TestContainer { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("/Child/Children/missing", PathStyle.Route);

        // Assert
        Assert.Null(result.Subject);
        Assert.Same(child, result.DeepestSubject);
        Assert.Equal(new PropertyReference(child, nameof(TestContainer.Children)), result.NextProperty);
        Assert.Same(child, Assert.Single(result.Steps).Subject);
    }

    [Fact]
    public void WhenInlinePathKeyIsMissing_ThenReturnsInlinePathsProperty()
    {
        // Arrange
        var folder = new TestContainerWithChildren { Name = "Folder" };
        var root = new TestReferenceRoot(Context);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Folder"] = folder };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("Folder/Sensor", PathStyle.Route);

        // Assert
        Assert.Null(result.Subject);
        Assert.Same(folder, result.DeepestSubject);
        Assert.Equal(new PropertyReference(folder, nameof(TestContainerWithChildren.Children)), result.NextProperty);
        var step = Assert.Single(result.Steps);
        Assert.Equal(nameof(TestReferenceRoot.Children), step.Property.Name);
        Assert.Equal("Folder", step.Index);
        Assert.Same(folder, step.Subject);
    }

    [Fact]
    public void WhenCollectionIndexIsMissing_ThenReturnsCollectionProperty()
    {
        // Arrange
        var item = new TestContainer { Name = "Item" };
        var root = new TestContainerWithItems(Context) { Items = [item] };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("/Items/5", PathStyle.Route);

        // Assert
        Assert.Null(result.Subject);
        Assert.Same(root, result.DeepestSubject);
        Assert.Equal(new PropertyReference(root, nameof(TestContainerWithItems.Items)), result.NextProperty);
        Assert.Same(item, Resolver.ResolvePartially("/Items/0", PathStyle.Route).Subject);
    }

    [Fact]
    public void WhenCanonicalSegmentNamesCollectionWithoutIndex_ThenReturnsNoNextProperty()
    {
        // Arrange
        var root = new TestContainerWithItems(Context);
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("/Items", PathStyle.Canonical);

        // Assert
        Assert.Null(result.Subject);
        Assert.Same(root, result.DeepestSubject);
        Assert.Null(result.NextProperty);
    }

    [Fact]
    public void WhenPropertyIsUnknownAndSubjectHasNoInlinePaths_ThenReturnsNoNextProperty()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("/Unknown", PathStyle.Route);

        // Assert
        Assert.Null(result.Subject);
        Assert.Same(root, result.DeepestSubject);
        Assert.Null(result.NextProperty);
    }

    [Fact]
    public void WhenRouteEndsAtCollectionWithoutIndex_ThenReturnsNoNextProperty()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("/Children", PathStyle.Route);

        // Assert
        Assert.Null(result.Subject);
        Assert.Same(root, result.DeepestSubject);
        Assert.Null(result.NextProperty);
    }

    [Fact]
    public void WhenRootDoesNotExist_ThenReturnsNothing()
    {
        // Act
        var result = Resolver.ResolvePartially("/Child", PathStyle.Route);

        // Assert
        Assert.Null(result.Subject);
        Assert.Null(result.DeepestSubject);
        Assert.Null(result.NextProperty);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void WhenMissingSubjectsAppearOneLevelAtATime_ThenResolutionMovesDeeperUntilSubjectIsFound()
    {
        // Arrange
        var root = new TestReferenceRoot(Context);
        RootManager.Root = root;
        var initial = Resolver.ResolvePartially("Folder/Sensor", PathStyle.Route);

        // Act
        var folder = new TestContainerWithChildren { Name = "Folder" };
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Folder"] = folder };
        var afterFolder = Resolver.ResolvePartially("Folder/Sensor", PathStyle.Route);

        var sensor = new TestContainerWithChildren { Name = "Sensor" };
        folder.Children = new Dictionary<string, TestContainerWithChildren> { ["Sensor"] = sensor };
        var afterSensor = Resolver.ResolvePartially("Folder/Sensor", PathStyle.Route);

        // Assert
        Assert.Equal(new PropertyReference(root, nameof(TestReferenceRoot.Children)), initial.NextProperty);
        Assert.Same(folder, afterFolder.DeepestSubject);
        Assert.Equal(new PropertyReference(folder, nameof(TestContainerWithChildren.Children)), afterFolder.NextProperty);
        Assert.Same(sensor, afterSensor.Subject);
        Assert.Null(afterSensor.NextProperty);
    }

    [Fact]
    public void WhenPathNavigatesToParent_ThenResolvesFromParent()
    {
        // Arrange
        var child = new TestContainer { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolvePartially("../Children/missing", PathStyle.Route, child);

        // Assert
        Assert.Null(result.Subject);
        Assert.Same(root, result.DeepestSubject);
        Assert.Equal(new PropertyReference(root, nameof(TestContainer.Children)), result.NextProperty);
    }
}
