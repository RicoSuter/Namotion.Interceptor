using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver cache invalidation behavior.
/// </summary>
public class SubjectPathResolverCacheTests : SubjectPathResolverTestBase
{
    [Fact]
    public void GetPath_CacheInvalidatedOnDetach_ReturnsNull()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Get initial path (caches it)
        var path1 = Resolver.GetPath(child, PathStyle.Canonical);
        Assert.Equal("/Child", path1);

        // Act - detach and get again
        root.Child = null;
        var path2 = Resolver.GetPath(child, PathStyle.Canonical);

        // Assert
        Assert.Null(path2);
    }

    [Fact]
    public void GetPath_CacheInvalidatedOnAttach_ReturnsUpdatedPath()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Get initial path for detached subject (caches null)
        var path1 = Resolver.GetPath(child, PathStyle.Canonical);
        Assert.Null(path1);

        // Act - attach and get again
        root.Child = child;
        var path2 = Resolver.GetPath(child, PathStyle.Canonical);

        // Assert
        Assert.Equal("/Child", path2);
    }

    [Fact]
    public void ResolveSubject_CacheInvalidatedOnAttach_ResolvesNewChild()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Resolve before attachment (caches null)
        var result1 = Resolver.ResolveSubject("/Child", PathStyle.Canonical);
        Assert.Null(result1);

        // Act - attach and resolve again
        root.Child = child;
        var result2 = Resolver.ResolveSubject("/Child", PathStyle.Canonical);

        // Assert
        Assert.Same(child, result2);
    }

    [Fact]
    public void WhenDictionaryKeyChangesByReplacement_ThenForwardCacheIsInvalidated()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestContainer>
            {
                ["old"] = child
            }
        };
        RootManager.Root = root;

        Assert.Equal("/Children[old]", Resolver.GetPath(child, PathStyle.Canonical));

        // Act
        root.Children = new Dictionary<string, TestContainer>
        {
            ["new"] = child
        };

        // Assert
        Assert.Equal("/Children[new]", Resolver.GetPath(child, PathStyle.Canonical));
    }

    [Fact]
    public void WhenDictionaryKeyChangesByReplacement_ThenReverseCacheIsInvalidated()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestContainer>
            {
                ["old"] = child
            }
        };
        RootManager.Root = root;

        Assert.Same(child, Resolver.ResolveSubject("/Children[old]", PathStyle.Canonical));
        Assert.Null(Resolver.ResolveSubject("/Children[new]", PathStyle.Canonical));

        // Act
        root.Children = new Dictionary<string, TestContainer>
        {
            ["new"] = child
        };

        // Assert
        Assert.Null(Resolver.ResolveSubject("/Children[old]", PathStyle.Canonical));
        Assert.Same(child, Resolver.ResolveSubject("/Children[new]", PathStyle.Canonical));
    }

    [Fact]
    public void WhenSameDictionaryIsRekeyedAndReordered_ThenForwardCacheIsInvalidated()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var children = new Dictionary<string, TestContainer>
        {
            ["first"] = child,
            ["second"] = child
        };
        var root = new TestContainer(Context) { Name = "Root", Children = children };
        RootManager.Root = root;

        Assert.Equal("/Children[first]", Resolver.GetPath(child, PathStyle.Canonical));

        children.Clear();
        children["second"] = child;
        children["third"] = child;

        // Act
        root.Children = children;

        // Assert
        Assert.Equal("/Children[second]", Resolver.GetPath(child, PathStyle.Canonical));
    }

    [Fact]
    public void WhenSameDictionaryIsRekeyedAndReordered_ThenReverseCacheIsInvalidated()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var children = new Dictionary<string, TestContainer>
        {
            ["first"] = child,
            ["second"] = child
        };
        var root = new TestContainer(Context) { Name = "Root", Children = children };
        RootManager.Root = root;

        Assert.Same(child, Resolver.ResolveSubject("/Children[first]", PathStyle.Canonical));
        Assert.Null(Resolver.ResolveSubject("/Children[third]", PathStyle.Canonical));

        children.Clear();
        children["second"] = child;
        children["third"] = child;

        // Act
        root.Children = children;

        // Assert
        Assert.Null(Resolver.ResolveSubject("/Children[first]", PathStyle.Canonical));
        Assert.Same(child, Resolver.ResolveSubject("/Children[third]", PathStyle.Canonical));
    }

    [Fact]
    public async Task WhenRelationshipChangesWhileAPathIsBeingCached_ThenStaleGenerationCannotBeRepublished()
    {
        // Arrange
        using var pathConstructionEntered = new ManualResetEventSlim();
        using var releasePathConstruction = new ManualResetEventSlim();
        var oldKey = new BlockingPathKey("old", pathConstructionEntered, releasePathConstruction);
        var newKey = new BlockingPathKey("new");
        var child = new PathCacheContainer(Context);
        var root = new PathCacheContainer(Context)
        {
            Children = new Dictionary<object, PathCacheContainer>
            {
                [oldKey] = child
            }
        };
        RootManager.Root = root;

        var overlappingRead = Task.Run(() => Resolver.GetPath(child, PathStyle.Canonical));
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => pathConstructionEntered.IsSet || overlappingRead.IsCompleted,
                message: "The path reader did not reach the blocking dictionary key.");
            if (overlappingRead.IsCompleted)
            {
                await overlappingRead;
            }

            // Act
            root.Children = new Dictionary<object, PathCacheContainer>
            {
                [newKey] = child
            };
        }
        finally
        {
            releasePathConstruction.Set();
            await AsyncTestHelpers.WaitUntilAsync(
                () => overlappingRead.IsCompleted,
                message: "The overlapping path reader did not complete after it was released.");
        }

        var overlappingPath = await overlappingRead;

        // Assert
        Assert.Equal("/Children[old]", overlappingPath);
        Assert.Equal("/Children[new]", Resolver.GetPath(child, PathStyle.Canonical));
    }

    private sealed class BlockingPathKey(
        string value,
        ManualResetEventSlim? pathConstructionEntered = null,
        ManualResetEventSlim? releasePathConstruction = null)
    {
        public override string ToString()
        {
            pathConstructionEntered?.Set();
            releasePathConstruction?.Wait();
            return value;
        }
    }
}

[InterceptorSubject]
internal partial class PathCacheContainer
{
    public partial Dictionary<object, PathCacheContainer> Children { get; set; }

    public PathCacheContainer()
    {
        Children = [];
    }
}
