using HomeBlaze.Services.Tests.Models;

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
}
