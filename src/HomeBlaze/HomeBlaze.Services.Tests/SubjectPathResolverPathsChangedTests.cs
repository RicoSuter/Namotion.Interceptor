using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for the event that tells a subscriber of SubjectPathResolver that a path may lead somewhere else.
/// </summary>
public class SubjectPathResolverPathsChangedTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenSubjectIsAttachedToTheGraph_ThenPathsChangedIsRaised()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        var raisedCount = 0;
        Resolver.PathsChanged += () => raisedCount++;

        // Act
        root.Child = child;

        // Assert
        Assert.True(raisedCount > 0);
    }

    [Fact]
    public void WhenSubjectIsDetachedFromTheGraph_ThenPathsChangedIsRaised()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        var raisedCount = 0;
        Resolver.PathsChanged += () => raisedCount++;

        // Act
        root.Child = null;

        // Assert
        Assert.True(raisedCount > 0);
    }

    [Fact]
    public void WhenEntryOfADictionaryIsReplaced_ThenPathsChangedIsRaised()
    {
        // Arrange
        var original = new TestContainer(Context) { Name = "Original" };
        var replacement = new TestContainer(Context) { Name = "Replacement" };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainer> { ["device"] = original };
        RootManager.Root = root;

        var raisedCount = 0;
        Resolver.PathsChanged += () => raisedCount++;

        // Act
        root.Children = new Dictionary<string, TestContainer> { ["device"] = replacement };

        // Assert
        Assert.True(raisedCount > 0);
    }

    [Fact]
    public void WhenValueOfAPropertyChanges_ThenPathsChangedIsNotRaised()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        var raisedCount = 0;
        Resolver.PathsChanged += () => raisedCount++;

        // Act
        root.Name = "Renamed root";
        child.Name = "Renamed child";

        // Assert
        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void WhenPathsChangedIsRaised_ThenACachedPathAlreadyResolvesToItsNewTarget()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // The path leads nowhere yet, which the resolver caches.
        Assert.Null(Resolver.ResolveSubject("/Child", PathStyle.Canonical));

        IInterceptorSubject? resolvedInHandler = null;
        Resolver.PathsChanged += () => resolvedInHandler = Resolver.ResolveSubject("/Child", PathStyle.Canonical);

        // Act
        root.Child = child;

        // Assert
        Assert.Same(child, resolvedInHandler);
    }

    [Fact]
    public void WhenHandlerIsRemoved_ThenItIsNotCalledAnymore()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        var raisedCount = 0;
        void OnPathsChanged() => raisedCount++;
        Resolver.PathsChanged += OnPathsChanged;
        Resolver.PathsChanged -= OnPathsChanged;

        // Act
        root.Child = new TestContainer(Context) { Name = "Child" };

        // Assert
        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void WhenSubjectIsAttachedToTheGraph_ThenPathsVersionChanges()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;
        var before = Resolver.PathsVersion;

        // Act
        root.Child = new TestContainer(Context) { Name = "Child" };

        // Assert
        Assert.NotEqual(before, Resolver.PathsVersion);
    }

    [Fact]
    public void WhenAPropertyIsAddedToASubjectAtRunTime_ThenPathsVersionChanges()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;
        var before = Resolver.PathsVersion;

        // Act
        root.TryGetRegisteredSubject()!.AddProperty("Dynamic", typeof(int), _ => 1, null);

        // Assert
        Assert.NotEqual(before, Resolver.PathsVersion);
    }
}
