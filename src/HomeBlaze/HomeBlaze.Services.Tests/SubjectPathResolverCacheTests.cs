using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver cache invalidation behavior.
/// </summary>
public class SubjectPathResolverCacheTests : SubjectPathResolverTestBase
{
    [Fact]
    public void GetPath_CacheInvalidatedOnDetach_ReturnsUpdatedPath()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };

        // Get initial path (caches it)
        var path1 = Resolver.GetPath(child);
        Assert.Equal("Child", path1);

        // Act - detach and get again
        root.Child = null;
        var path2 = Resolver.GetPath(child);

        // Assert
        Assert.Null(path2);
    }

    [Fact]
    public void GetPath_CacheInvalidatedOnAttach_ReturnsUpdatedPath()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };

        // Get initial path for detached subject (caches null)
        var path1 = Resolver.GetPath(child);
        Assert.Null(path1);

        // Act - attach and get again
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        var path2 = Resolver.GetPath(child);

        // Assert
        Assert.Equal("Child", path2);
    }
}
