using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Derived;
using Namotion.Interceptor.Tracking.Recorder;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for a derived property whose getter resolves a path: it depends on what the path passes through, and not on
/// changes of the structure elsewhere in the graph.
/// </summary>
public class SubjectPathResolverDerivedTests : SubjectPathResolverTestBase
{
    [Theory]
    [InlineData("Absolute", false)]
    [InlineData("Absolute", true)]
    [InlineData("Relative", false)]
    [InlineData("Parent", false)]
    [InlineData("Path", false)]
    [InlineData("Path", true)]
    public void WhenSubjectsElsewhereAreAttachedMovedAndDetached_ThenADerivedPropertyThatResolvesAPathIsNotEvaluatedAgain(string kind, bool isServedFromCache)
    {
        // Arrange
        var graph = CreateGraph();
        var watcher = CreateWatcher(CreateResolve(kind, graph), isServedFromCache);
        var evaluations = watcher.EvaluationCount;
        var version = Resolver.PathsVersion;

        // Act
        var list = new TestContainerWithItemList(Context) { Items = [new TestContainer(Context), new TestContainer(Context)] };
        list.Items = [list.Items[1], list.Items[0]];
        graph.Root.Children = new Dictionary<string, TestContainer> { ["start"] = graph.Start, ["sibling"] = new(Context) };
        list.Items = [];

        // Assert
        Assert.NotEqual(version, Resolver.PathsVersion);
        Assert.Equal(evaluations, watcher.EvaluationCount);
    }

    [Theory]
    [InlineData("Absolute", false)]
    [InlineData("Absolute", true)]
    [InlineData("Relative", false)]
    [InlineData("Parent", false)]
    [InlineData("Path", false)]
    [InlineData("Path", true)]
    public void WhenTheTargetIsReplaced_ThenADerivedPropertyThatResolvesAPathHasTheNewResult(string kind, bool isServedFromCache)
    {
        // Arrange
        var graph = CreateGraph();
        var resolve = CreateResolve(kind, graph);
        var watcher = CreateWatcher(resolve, isServedFromCache);
        var before = watcher.LastResult;

        // Act
        graph.Child.Child = new TestContainer(Context) { Name = "Replacement" };

        // Assert
        Assert.NotEqual(before, watcher.LastResult);
        Assert.Equal(resolve(), watcher.LastResult);
    }

    [Theory]
    [InlineData("Reference", false)]
    [InlineData("Reference", true)]
    [InlineData("DynamicProperty", false)]
    [InlineData("DynamicProperty", true)]
    public void WhenTheTargetAppearsLater_ThenADerivedPropertyThatResolvesAPathHasIt(string shape, bool isServedFromCache)
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        RootManager.Root = new TestContainer(Context) { Name = "Root", Child = child };
        var target = new TestContainer(Context) { Name = "Target" };
        var path = shape == "Reference" ? "/Child/Child" : "/Child/Extra";
        var watcher = CreateWatcher(() => Resolver.ResolveSubject(path, PathStyle.Canonical), isServedFromCache);
        Assert.Null(watcher.LastResult);

        // Act
        if (shape == "Reference")
            child.Child = target;
        else
            child.TryGetRegisteredSubject()!.AddProperty("Extra", typeof(TestContainer), _ => target, null);

        // Assert
        Assert.Same(target, watcher.LastResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubjectsOnTheRouteSwapPlacesInAList_ThenADerivedPropertyThatGetsAPathHasTheNewPath(bool isServedFromCache)
    {
        // Arrange
        var first = new TestContainer(Context) { Name = "First" };
        var second = new TestContainer(Context) { Name = "Second" };
        var list = new TestContainerWithItemList(Context) { Items = [first, second] };
        RootManager.Root = list;
        var watcher = CreateWatcher(() => Resolver.GetPath(first, PathStyle.Canonical), isServedFromCache);
        Assert.Equal("/Items[0]", watcher.LastResult);

        // Act
        list.Items = [second, first];

        // Assert
        Assert.Equal("/Items[1]", watcher.LastResult);
    }

    [Fact]
    public void WhenTheStartOfAPathThatLeadsUpMovesToAnotherParent_ThenADerivedPropertyResolvesFromTheNewParent()
    {
        // Arrange
        var graph = CreateGraph();
        var watcher = CreateWatcher(CreateResolve("Parent", graph), isServedFromCache: false);
        Assert.Same(graph.Target, watcher.LastResult);

        // Act
        graph.Root.Children = new Dictionary<string, TestContainer>();
        graph.Child.Children = new Dictionary<string, TestContainer> { ["start"] = graph.Start };

        // Assert: up from the start is Child now, whose Child has no Child
        Assert.Null(watcher.LastResult);
    }

    [Fact]
    public void WhenTheRootIsAssignedAndTheStructureChangesElsewhere_ThenGetPathOfTheRootIsComputedAgain()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        var watcher = CreateWatcher(() => Resolver.GetPath(root, PathStyle.Canonical), isServedFromCache: false);
        Assert.Null(watcher.LastResult);

        // Act
        RootManager.Root = root;
        _ = new TestContainerWithItemList(Context) { Items = [new TestContainer(Context)] };

        // Assert
        Assert.Equal("/", watcher.LastResult);
    }

    [Fact]
    public void WhenADerivedPropertyResolvedAMissingName_ThenEveryRequiredPropertyHasMetadata()
    {
        // Arrange
        RootManager.Root = new TestContainer(Context) { Name = "Root" };
        var watcher = CreateWatcher(() => Resolver.ResolveSubject("/Missing", PathStyle.Canonical), isServedFromCache: false);

        // Act
        var required = new PropertyReference(watcher, nameof(PathWatcher.Resolved)).GetRequiredProperties().ToArray();

        // Assert
        Assert.All(required, property => Assert.NotNull(property.Metadata.Name));
    }

    [Fact]
    public void WhenAPathIsResolvedWhileReadsAreRecorded_ThenThePathsVersionIsRecorded()
    {
        // Arrange
        CreateGraph();
        var scope = ReadPropertyRecorder.Start();

        // Act
        Resolver.ResolveSubject("/Child/Child", PathStyle.Canonical);
        var recorded = scope.GetPropertiesAndDispose();

        // Assert: this record is what makes a component render again when the paths change
        Assert.Contains(recorded, property => property.Subject is SubjectPathsVersion);
    }

    private sealed record Graph(TestContainer Root, TestContainer Child, TestContainer Target, TestContainer Start);

    // Root/Child/Child is the target, and Root/Children[start] the start of a path that leads up.
    private Graph CreateGraph()
    {
        var target = new TestContainer(Context) { Name = "Target" };
        var child = new TestContainer(Context) { Name = "Child", Child = target };
        var start = new TestContainer(Context) { Name = "Start" };
        var root = new TestContainer(Context) { Name = "Root", Child = child, Children = new Dictionary<string, TestContainer> { ["start"] = start } };
        RootManager.Root = root;
        return new Graph(root, child, target, start);
    }

    private Func<object?> CreateResolve(string kind, Graph graph) => kind switch
    {
        "Absolute" => () => Resolver.ResolveSubject("/Child/Child", PathStyle.Canonical),
        "Relative" => () => Resolver.ResolveSubject("./Child", PathStyle.Canonical, graph.Child),
        "Parent" => () => Resolver.ResolveSubject("../Child/Child", PathStyle.Canonical, graph.Start),
        "Path" => () => Resolver.GetPath(graph.Target, PathStyle.Canonical),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    // The first evaluation caches an absolute path or the paths of a subject with its route, so that the second one is
    // served from the cache.
    private PathWatcher CreateWatcher(Func<object?> resolve, bool isServedFromCache)
    {
        var watcher = new PathWatcher(Context) { Resolve = resolve };
        watcher.Trigger++;
        if (isServedFromCache)
            watcher.Trigger++;

        return watcher;
    }
}
