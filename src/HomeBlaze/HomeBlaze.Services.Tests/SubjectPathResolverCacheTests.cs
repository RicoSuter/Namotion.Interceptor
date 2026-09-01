using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver cache invalidation behavior.
/// </summary>
public class SubjectPathResolverCacheTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenEmptyInvalidationsAreRepeated_ThenTheResolverDoesNotAllocatePerInvalidation()
    {
        // Arrange
        const int lifecycleChangeCount = 2_000;
        var subject = new PathCacheContainer(Context);
        var changes = new SubjectLifecycleChange[lifecycleChangeCount];
        for (var index = 0; index < changes.Length; index++)
        {
            changes[index] = new SubjectLifecycleChange
            {
                Subject = subject,
                ReferenceCount = index + 1,
                IsPropertyReferenceAdded = true
            };
        }

        var relationshipHandler = (IPropertyRelationshipHandler)Resolver;
        var relationshipProperty = new PropertyReference(subject, nameof(PathCacheContainer.Children));

        // Warm the JIT and interface dispatch before measuring the same-thread hot path.
        Resolver.HandleLifecycleChange(changes[0]);
        relationshipHandler.ReconcileChildRelationships(
            relationshipProperty, ReadOnlySpan<SubjectPropertyRelationship>.Empty);

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < changes.Length; index++)
        {
            Resolver.HandleLifecycleChange(changes[index]);
        }
        relationshipHandler.ReconcileChildRelationships(
            relationshipProperty, ReadOnlySpan<SubjectPropertyRelationship>.Empty);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenDistinctSubjectsAreEqualByValue_ThenCanonicalPathsAreCachedByReferenceIdentity()
    {
        // Arrange
        var first = new EqualByValuePathContainer(Context);
        var second = new EqualByValuePathContainer(Context);
        var root = new EqualByValuePathRoot(Context)
        {
            First = first,
            Second = second
        };
        RootManager.Root = root;
        EqualByValuePathContainer.ResetEqualityCallCount();

        // Act
        var firstPath = Resolver.GetPath(first, PathStyle.Canonical);
        var secondPath = Resolver.GetPath(second, PathStyle.Canonical);

        // Assert
        Assert.Equal("/First", firstPath);
        Assert.Equal("/Second", secondPath);
        Assert.Equal(0, EqualByValuePathContainer.EqualityCallCount);
    }

    [Fact]
    public void WhenAnAncestorIsEqualByValueToItsDescendant_ThenPathTraversalUsesReferenceIdentity()
    {
        // Arrange
        var descendant = new EqualByValuePathContainer(Context);
        var ancestor = new EqualByValuePathContainer(Context) { Child = descendant };
        var root = new EqualByValuePathRoot(Context) { Child = ancestor };
        RootManager.Root = root;
        EqualByValuePathContainer.ResetEqualityCallCount();

        // Act
        var path = Resolver.GetPath(descendant, PathStyle.Canonical);

        // Assert
        Assert.Equal("/Child/Child", path);
        Assert.Equal(0, EqualByValuePathContainer.EqualityCallCount);
    }

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
    public async Task WhenRelationshipChangesWhileAPathIsBeingCached_ThenStaleVersionCannotPoisonQuiescentLookup()
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

[InterceptorSubject]
internal partial class EqualByValuePathContainer
{
    private static int _equalityCallCount;

    public static int EqualityCallCount => Volatile.Read(ref _equalityCallCount);

    public partial EqualByValuePathContainer? Child { get; set; }

    public static void ResetEqualityCallCount()
    {
        Volatile.Write(ref _equalityCallCount, 0);
    }

    public override bool Equals(object? obj)
    {
        Interlocked.Increment(ref _equalityCallCount);
        return obj is EqualByValuePathContainer;
    }

    public override int GetHashCode()
    {
        Interlocked.Increment(ref _equalityCallCount);
        return 0;
    }
}

[InterceptorSubject]
internal partial class EqualByValuePathRoot
{
    public partial EqualByValuePathContainer? First { get; set; }

    public partial EqualByValuePathContainer? Second { get; set; }

    public partial EqualByValuePathContainer? Child { get; set; }
}
