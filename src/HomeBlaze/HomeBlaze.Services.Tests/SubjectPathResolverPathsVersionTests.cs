using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for the version that tells a component of SubjectPathResolver that a path may lead somewhere else.
/// </summary>
public class SubjectPathResolverPathsVersionTests : SubjectPathResolverTestBase
{
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
    public void WhenSubjectIsDetachedFromTheGraph_ThenPathsVersionChanges()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root", Child = new TestContainer(Context) { Name = "Child" } };
        RootManager.Root = root;
        var before = Resolver.PathsVersion;

        // Act
        root.Child = null;

        // Assert
        Assert.NotEqual(before, Resolver.PathsVersion);
    }

    [Fact]
    public void WhenEntryOfADictionaryIsReplaced_ThenPathsVersionChanges()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainer> { ["device"] = new(Context) { Name = "Original" } };
        RootManager.Root = root;
        var before = Resolver.PathsVersion;

        // Act
        root.Children = new Dictionary<string, TestContainer> { ["device"] = new(Context) { Name = "Replacement" } };

        // Assert
        Assert.NotEqual(before, Resolver.PathsVersion);
    }

    [Fact]
    public void WhenValueOfAPropertyChanges_ThenPathsVersionStaysTheSame()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;
        var before = Resolver.PathsVersion;

        // Act
        root.Name = "Renamed root";
        child.Name = "Renamed child";

        // Assert
        Assert.Equal(before, Resolver.PathsVersion);
    }

    [Fact]
    public void WhenPathsVersionHasChanged_ThenACachedPathResolvesToItsNewTarget()
    {
        // Arrange: the path leads nowhere yet, which the resolver caches.
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;
        Assert.Null(Resolver.ResolveSubject("/Child", PathStyle.Canonical));
        var before = Resolver.PathsVersion;

        // Act
        root.Child = child;

        // Assert
        Assert.NotEqual(before, Resolver.PathsVersion);
        Assert.Same(child, Resolver.ResolveSubject("/Child", PathStyle.Canonical));
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
