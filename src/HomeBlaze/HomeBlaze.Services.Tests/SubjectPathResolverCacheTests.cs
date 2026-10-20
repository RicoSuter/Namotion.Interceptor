using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

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
    public void WhenSubjectIsAttachedWhileItsPathIsResolved_ThenLaterResolveReturnsIt()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        var hook = new AfterReadHook();
        Context.AddService(hook);
        hook.Arm(nameof(TestContainer.Child), () => root.Child = child);

        // Act
        var resolvedDuringAttach = Resolver.ResolveSubject("/Child", PathStyle.Canonical);
        var resolvedAfterAttach = Resolver.ResolveSubject("/Child", PathStyle.Canonical);

        // Assert
        Assert.Null(resolvedDuringAttach);
        Assert.Same(child, resolvedAfterAttach);
    }

    [Fact]
    public void WhenSubjectIsDetachedWhileItsPathIsResolved_ThenLaterResolveReturnsNull()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        var hook = new AfterReadHook();
        Context.AddService(hook);
        hook.Arm(nameof(TestContainer.Child), () => root.Child = null);

        // Act
        var resolvedDuringDetach = Resolver.ResolveSubject("/Child", PathStyle.Canonical);
        var resolvedAfterDetach = Resolver.ResolveSubject("/Child", PathStyle.Canonical);

        // Assert
        Assert.Same(child, resolvedDuringDetach);
        Assert.Null(resolvedAfterDetach);
    }

    [Fact]
    public void WhenSubjectIsDetachedWhileItsPathIsBuilt_ThenLaterGetPathReturnsNull()
    {
        // Arrange
        var key = new CallbackKey("a");
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainerWithCallbackKeys(Context)
        {
            Items = new Dictionary<CallbackKey, TestContainer> { [key] = child }
        };
        RootManager.Root = root;
        key.OnFormat = () => root.Items = new Dictionary<CallbackKey, TestContainer>();

        // Act
        var pathDuringDetach = Resolver.GetPath(child, PathStyle.Canonical);
        var pathAfterDetach = Resolver.GetPath(child, PathStyle.Canonical);

        // Assert
        Assert.Equal("/Items[a]", pathDuringDetach);
        Assert.Null(pathAfterDetach);
    }

    /// <summary>
    /// Runs an action once, right after the next read of the named property, so that a graph change lands between
    /// what a resolution read and the moment it caches its result.
    /// </summary>
    private sealed class AfterReadHook : IReadInterceptor
    {
        private string? _propertyName;
        private Action? _action;

        public void Arm(string propertyName, Action action)
        {
            _propertyName = propertyName;
            _action = action;
        }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var value = next(ref context);
            if (_action is { } action && context.Property.Name == _propertyName)
            {
                _action = null;
                action();
            }

            return value;
        }
    }
}
