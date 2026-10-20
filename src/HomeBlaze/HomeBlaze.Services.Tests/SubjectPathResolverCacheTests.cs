using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

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
    public void WhenSubjectIsDetachedWhileItsPathIsResolved_ThenLaterResolveReturnsNull()
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
        var resolvedDuringDetach = Resolver.ResolveSubject("/Items[a]", PathStyle.Canonical);
        var resolvedAfterDetach = Resolver.ResolveSubject("/Items[a]", PathStyle.Canonical);

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

    [Fact]
    public void WhenListIsReordered_ThenPathsAndResolutionFollowTheNewOrder()
    {
        // Arrange
        var first = new TestContainer(Context) { Name = "First" };
        var second = new TestContainer(Context) { Name = "Second" };
        var root = new TestContainerWithItemList(Context) { Items = [first, second] };
        RootManager.Root = root;
        var pathBefore = Resolver.GetPath(first, PathStyle.Canonical);
        var resolvedBefore = Resolver.ResolveSubject("/Items[0]", PathStyle.Canonical);
        var versionBefore = Resolver.PathsVersion;

        // Act
        root.Items = [second, first];

        // Assert
        Assert.Equal("/Items[0]", pathBefore);
        Assert.Same(first, resolvedBefore);
        Assert.NotEqual(versionBefore, Resolver.PathsVersion);
        Assert.Equal("/Items[1]", Resolver.GetPath(first, PathStyle.Canonical));
        Assert.Same(second, Resolver.ResolveSubject("/Items[0]", PathStyle.Canonical));
    }

    [Fact]
    public void WhenPathIsBuiltBeforeTheListIndicesAreRefreshed_ThenLaterGetPathReturnsTheNewIndex()
    {
        // Arrange
        var retained = new TestContainer(Context) { Name = "Retained" };
        var inserted = new TestContainer(Context) { Name = "Inserted" };
        var root = new TestContainerWithItemList(Context) { Items = [retained] };
        RootManager.Root = root;

        // Runs after the resolver handled the attach and before the registry refreshes the index of the retained entry.
        string? pathBeforeRefresh = null;
        Context.AddService(new AttachHook(inserted, () => pathBeforeRefresh = Resolver.GetPath(retained, PathStyle.Canonical)));

        // Act
        root.Items = [inserted, retained];
        var pathAfterRefresh = Resolver.GetPath(retained, PathStyle.Canonical);

        // Assert
        Assert.Equal("/Items[0]", pathBeforeRefresh);
        Assert.Equal("/Items[1]", pathAfterRefresh);
    }

    [Fact]
    public void WhenPropertyIsAddedAtRunTimeWithTheNameOfAnInlinePath_ThenResolveFollowsTheProperty()
    {
        // Arrange
        var child = new TestContainerWithChildren(Context) { Name = "Child" };
        var root = new TestContainerWithChildren(Context)
        {
            Children = new Dictionary<string, TestContainerWithChildren> { ["Shadowed"] = child }
        };
        RootManager.Root = root;
        var resolvedBefore = Resolver.ResolveSubject("/Shadowed", PathStyle.Canonical);

        // Act
        root.TryGetRegisteredSubject()!.AddProperty("Shadowed", typeof(TestContainerWithChildren), _ => null, null);
        var resolvedAfter = Resolver.ResolveSubject("/Shadowed", PathStyle.Canonical);

        // Assert
        Assert.Same(child, resolvedBefore);
        Assert.Null(resolvedAfter);
    }

    [Fact]
    public void WhenResolverIsAddedBeforeTheRegistry_ThenItsLifecycleHandlersRunAfterTheRegistry()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new SubjectPathResolver(() => null));

        // Act
        context.WithRegistry();

        // Assert
        var lifecycleHandlers = context.GetServices<ILifecycleHandler>().ToList();
        Assert.True(
            lifecycleHandlers.FindIndex(handler => handler is SubjectRegistry) <
            lifecycleHandlers.FindIndex(handler => handler is SubjectPathResolver));

        var propertyLifecycleHandlers = context.GetServices<IPropertyLifecycleHandler>().ToList();
        Assert.True(
            propertyLifecycleHandlers.FindIndex(handler => handler is SubjectRegistry) <
            propertyLifecycleHandlers.FindIndex(handler => handler is SubjectPathResolver));
    }

    /// <summary>
    /// Runs an action once, when the subject is attached, after the lifecycle handlers that were added before it.
    /// </summary>
    private sealed class AttachHook(IInterceptorSubject subject, Action action) : ILifecycleHandler
    {
        private Action? _action = action;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (_action is { } pending && change.IsPropertyReferenceAdded && change.Subject == subject)
            {
                _action = null;
                pending();
            }
        }
    }
}
