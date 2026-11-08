using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for the graph route that <see cref="SubjectPathResolver"/> reports to an <see cref="ISubjectPathRecorder"/>,
/// which is what a resolution depends on besides the path. Every resolution with a recorder gives the result of the
/// resolution without one.
/// </summary>
public class SubjectPathRecorderTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenAnAbsolutePathPassesSubjectReferences_ThenTheRouteHasEachOfThem()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("/Child/Child");

        // Assert
        Assert.Same(grandchild, subject);
        Assert.Equal([Property(root, nameof(TestContainer.Child)), Property(child, nameof(TestContainer.Child))], route.Properties);
        Assert.False(route.IsUnbounded);
        Assert.Empty(route.ParentSteps);
    }

    [Theory]
    [InlineData("./Child")]
    [InlineData("Child")]
    public void WhenARelativePathStartsAtASubject_ThenTheRouteHasThePropertiesFromThatSubject(string path)
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject(path, child);

        // Assert
        Assert.Same(grandchild, subject);
        Assert.Equal([Property(child, nameof(TestContainer.Child))], route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public void WhenAPathLeadsUp_ThenTheRouteHasAParentStepPerStepWithTheParentsOfTheSubject()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("../../Name", grandchild);

        // Assert
        Assert.Null(subject);
        Assert.Equal([grandchild, child], route.ParentSteps.Select(step => step.Subject));
        Assert.Equal([Property(child, nameof(TestContainer.Child))], route.ParentSteps[0].Parents.Select(parent => parent.Property.Reference));
        Assert.Equal([Property(root, nameof(TestContainer.Child))], route.ParentSteps[1].Parents.Select(parent => parent.Property.Reference));
        Assert.Empty(route.Properties);
        Assert.False(route.IsUnbounded);
    }

    // The folder above one holder is detached, which writes no holder yet leaves the child with a single parent.
    [Fact]
    public void WhenAPathLeadsUpFromASubjectWithSeveralParents_ThenTheRouteHasAParentStepWithThem()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var folder = new TestContainer(Context) { Name = "Folder", Child = child };
        var root = new TestContainer(Context) { Name = "Root", Child = folder, Children = new Dictionary<string, TestContainer> { ["copy"] = child } };
        RootManager.Root = root;

        // Act
        var (ambiguous, route) = ResolveSubject("..", child);
        root.Child = null;
        var (single, _) = ResolveSubject("..", child);

        // Assert
        Assert.Null(ambiguous);
        Assert.Same(root, single);
        var step = Assert.Single(route.ParentSteps);
        Assert.Same(child, step.Subject);
        Assert.Equal([Property(folder, nameof(TestContainer.Child)), Property(root, nameof(TestContainer.Children))], step.Parents.Select(parent => parent.Property.Reference));
        Assert.Empty(route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public void WhenAPathLeadsUpFromAnUnregisteredSubject_ThenTheRouteIsUnbounded()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;
        var detached = new TestContainer { Name = "Detached" };

        // Act
        var (subject, route) = ResolveSubject("..", detached);

        // Assert
        Assert.Null(subject);
        Assert.True(route.IsUnbounded);
    }

    [Fact]
    public void WhenAPathIndexesACollection_ThenTheRouteHasTheCollectionProperty()
    {
        // Arrange
        var item = new TestContainer(Context) { Name = "Item" };
        var root = new TestContainerWithItemList(Context) { Items = [new TestContainer(Context), item] };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("/Items[1]");

        // Assert
        Assert.Same(item, subject);
        Assert.Equal([Property(root, nameof(TestContainerWithItemList.Items))], route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public void WhenAPathKeysADictionary_ThenTheRouteHasTheDictionaryProperty()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Children = new Dictionary<string, TestContainer> { ["key"] = child } };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("/Children[key]");

        // Assert
        Assert.Same(child, subject);
        Assert.Equal([Property(root, nameof(TestContainer.Children))], route.Properties);
    }

    [Fact]
    public void WhenAPathKeysAMissingEntry_ThenTheRouteHasTheDictionaryProperty()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("/Children[missing]/Name");

        // Assert
        Assert.Null(subject);
        Assert.Equal([Property(root, nameof(TestContainer.Children))], route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public void WhenAPathUsesAnInlinePathsChild_ThenTheRouteHasTheMissingPropertyAndTheChildrenProperty()
    {
        // Arrange
        var child = new TestContainerWithChildren(Context) { Name = "Child" };
        var root = new TestContainerWithChildren(Context) { Name = "Root", Children = new Dictionary<string, TestContainerWithChildren> { ["kitchen"] = child } };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("/kitchen");

        // Assert
        Assert.Same(child, subject);
        Assert.Equal([Property(root, "kitchen"), Property(root, nameof(TestContainerWithChildren.Children))], route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public void WhenAPropertyAndAnInlinePathsChildHaveTheSameName_ThenTheRouteHasThePropertyOnly()
    {
        // Arrange
        var byProperty = new TestContainerWithChildren(Context) { Name = "ByProperty" };
        var byChildren = new TestContainerWithChildren(Context) { Name = "ByChildren" };
        var root = new TestContainerWithChildren(Context)
        {
            Name = "Root",
            Child = byProperty,
            Children = new Dictionary<string, TestContainerWithChildren> { ["Child"] = byChildren }
        };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("/Child");

        // Assert
        Assert.Same(byProperty, subject);
        Assert.Equal([Property(root, nameof(TestContainerWithChildren.Child))], route.Properties);
    }

    [Fact]
    public void WhenASegmentIsMissing_ThenTheRouteHasThePropertyThatAddingItChanges()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("/Garden/Name");

        // Assert
        Assert.Null(subject);
        Assert.Equal([Property(root, "Garden")], route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public void WhenASegmentIsEscaped_ThenTheRouteHasThePropertyOfTheUnescapedSegment()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Children = new Dictionary<string, TestContainer> { ["My File.md"] = child } };
        RootManager.Root = root;

        // Act
        var (subject, route) = ResolveSubject("/Children[My%20File.md]");

        // Assert
        Assert.Same(child, subject);
        Assert.Equal([Property(root, nameof(TestContainer.Children))], route.Properties);
    }

    [Fact]
    public void WhenAPathEndsAtAnAttributeProperty_ThenTheRouteHasTheHoldersOnly()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;
        child.TryGetRegisteredProperty(nameof(TestContainer.Name))!.AddAttribute("Unit", typeof(string), _ => "m", null);

        // Act
        var (subject, route) = ResolveSubject("/Child/Name@Unit");

        // Assert
        Assert.Null(subject);
        Assert.Equal([Property(root, nameof(TestContainer.Child))], route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Child")]
    public void WhenTheRootIsMissing_ThenTheRouteOfAnAbsolutePathIsUnbounded(string path)
    {
        // Arrange
        RootManager.Root = null;

        // Act
        var (subject, route) = ResolveSubject(path);

        // Assert
        Assert.Null(subject);
        Assert.True(route.IsUnbounded);
    }

    [Fact]
    public void WhenAnAbsolutePathIsResolvedAgain_ThenTheCachedResolutionReportsTheSameRoute()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;
        var (_, first) = ResolveSubject("/Child/Garden");

        // Act
        var (subject, second) = ResolveSubject("/Child/Garden");

        // Assert
        Assert.Null(subject);
        Assert.Equal([Property(root, nameof(TestContainer.Child)), Property(child, "Garden")], first.Properties);
        Assert.Equal(first.Properties, second.Properties);
        Assert.Equal(first.IsUnbounded, second.IsUnbounded);
    }

    [Fact]
    public void WhenAnAbsolutePathWasCachedWithoutARecorder_ThenAResolutionWithOneReportsTheRoute()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;
        var withoutRecorder = Resolver.ResolveSubject("/Child", PathStyle.Canonical);

        // Act
        var (subject, route) = ResolveSubject("/Child");

        // Assert
        Assert.Same(child, withoutRecorder);
        Assert.Same(child, subject);
        Assert.Equal([Property(root, nameof(TestContainer.Child))], route.Properties);
    }

    [Fact]
    public void WhenAnAbsolutePathIsResolvedWithoutARecorderAgain_ThenTheCacheHitAllocatesNothing()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;
        Resolver.ResolveSubject("/Child", PathStyle.Canonical);
        Resolver.GetPath(child, PathStyle.Canonical);

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var subject = Resolver.ResolveSubject("/Child", PathStyle.Canonical);
        var path = Resolver.GetPath(child, PathStyle.Canonical);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.Same(child, subject);
        Assert.Equal("/Child", path);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenThePathOfASubjectWithOneParentIsBuilt_ThenTheRouteHasAParentStepAndTheHoldersOfItsAncestors()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var (path, route) = GetPath(grandchild);

        // Assert
        Assert.Equal("/Child/Child", path);
        var step = Assert.Single(route.ParentSteps);
        Assert.Same(grandchild, step.Subject);
        Assert.Equal([Property(child, nameof(TestContainer.Child))], step.Parents.Select(parent => parent.Property.Reference));
        Assert.Equal([Property(child, nameof(TestContainer.Child)), Property(root, nameof(TestContainer.Child))], route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public void WhenThePathOfASubjectWithSeveralParentsIsBuilt_ThenTheRouteHasTheHoldersOfEveryPath()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var folder = new TestContainer(Context) { Name = "Folder", Child = child };
        var root = new TestContainer(Context) { Name = "Root", Child = folder, Children = new Dictionary<string, TestContainer> { ["shortcut"] = child } };
        RootManager.Root = root;

        // Act
        var (path, route) = GetPath(child);

        // Assert
        Assert.Equal("/Children[shortcut]", path);
        var step = Assert.Single(route.ParentSteps);
        Assert.Equal(2, step.Parents.Length);
        Assert.Equal(
            [Property(folder, nameof(TestContainer.Child)), Property(root, nameof(TestContainer.Child)), Property(root, nameof(TestContainer.Children))],
            route.Properties);
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public void WhenAnAncestorHasNoParent_ThenTheRouteOfThePathIsUnbounded()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var detached = new TestContainer(Context) { Name = "Detached", Child = child };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var (path, route) = GetPath(child);

        // Assert
        Assert.Equal("/Child", path);
        Assert.Equal([Property(detached, nameof(TestContainer.Child))], route.Properties);
        Assert.True(route.IsUnbounded);
    }

    [Fact]
    public void WhenThePathOfASubjectIsBuiltAgain_ThenTheCachedPathsReportTheSameRoute()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;
        var withoutRecorder = Resolver.GetPath(child, PathStyle.Canonical);
        var (_, first) = GetPath(child);

        // Act
        var (path, second) = GetPath(child);

        // Assert
        Assert.Equal("/Child", withoutRecorder);
        Assert.Equal("/Child", path);
        Assert.Equal([Property(root, nameof(TestContainer.Child))], first.Properties);
        Assert.Equal(first.Properties, second.Properties);
        Assert.Equal(first.ParentSteps.Count, second.ParentSteps.Count);
    }

    [Fact]
    public async Task WhenAParentIsDetachedWhileThePathsOfADescendantAreBuilt_ThenNoPathBuildFails()
    {
        // Arrange
        var leaf = new TestContainer(Context) { Name = "Leaf" };
        var middle = new TestContainer(Context) { Name = "Middle", Children = new Dictionary<string, TestContainer> { ["a"] = leaf } };
        var top = new TestContainer(Context) { Name = "Top", Child = middle };
        var root = new TestContainer(Context) { Name = "Root", Child = top };
        RootManager.Root = root;
        using var writing = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!writing.IsSet)
            {
                Resolver.GetPaths(leaf, PathStyle.Canonical);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        // Act
        for (var toggle = 0; toggle < 50_000; toggle++)
        {
            top.Child = toggle % 2 == 0 ? null : middle;
        }

        writing.Set();
        var reading = Task.WhenAll(readers);

        // Assert
        await reading;
        Assert.Equal(["/Child/Child/Children[a]"], Resolver.GetPaths(leaf, PathStyle.Canonical));
    }

    private (IInterceptorSubject? Subject, RecordedRoute Route) ResolveSubject(string path, IInterceptorSubject? relativeTo = null)
    {
        var route = new RecordedRoute();
        var subject = Resolver.ResolveSubject(path, PathStyle.Canonical, relativeTo, route);
        Assert.Same(Resolver.ResolveSubject(path, PathStyle.Canonical, relativeTo), subject);
        return (subject, route);
    }

    private (string? Path, RecordedRoute Route) GetPath(IInterceptorSubject subject)
    {
        var route = new RecordedRoute();
        var path = Resolver.GetPath(subject, PathStyle.Canonical, route);
        Assert.Equal(Resolver.GetPath(subject, PathStyle.Canonical), path);
        return (path, route);
    }

    private static PropertyReference Property(IInterceptorSubject subject, string name) => new(subject, name);

    private sealed class RecordedRoute : ISubjectPathRecorder
    {
        public List<PropertyReference> Properties { get; } = [];

        public List<(IInterceptorSubject Subject, ImmutableArray<SubjectPropertyParent> Parents)> ParentSteps { get; } = [];

        public bool IsUnbounded { get; private set; }

        public void AddProperty(PropertyReference property) => Properties.Add(property);

        public void AddParentStep(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents) => ParentSteps.Add((subject, parents));

        public void MarkUnbounded() => IsUnbounded = true;
    }
}
