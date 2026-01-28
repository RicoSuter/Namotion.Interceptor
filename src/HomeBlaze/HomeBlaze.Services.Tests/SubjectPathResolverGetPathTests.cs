using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver.GetPath() and GetPaths() methods.
/// </summary>
public class SubjectPathResolverGetPathTests : SubjectPathResolverTestBase
{
    [Fact]
    public void GetPath_RootSubject_ReturnsNull()
    {
        // Arrange - A subject with no parents is orphaned/detached - it has no path from anywhere
        var root = new TestContainer(Context) { Name = "Root" };

        // Act
        var path = Resolver.GetPath(root);

        // Assert - Even though we call it "root", from the path resolver's perspective,
        // if it has no parents, it's unreachable and has no path
        Assert.Null(path);
    }

    [Fact]
    public void GetPath_DirectChild_ReturnsPropertyName()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        // Act
        var path = Resolver.GetPath(child);

        // Assert
        Assert.Equal("Child", path);
    }

    [Fact]
    public void GetPath_NestedChild_ReturnsFullPath()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context)
        {
            Name = "Child",
            Child = grandchild
        };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        // Act
        var path = Resolver.GetPath(grandchild);

        // Assert - Default format is now bracket notation (Child.Child)
        Assert.Equal("Child.Child", path);
    }

    // NOTE: Dictionary-based path tests are skipped because the interceptor framework
    // doesn't automatically track lifecycle changes when dictionary items are added via indexer.
    // Dictionary modifications need explicit lifecycle management or observable collections.
    // However, ResolveSubject() still works fine with dictionaries (see ResolveSubject tests).

    [Fact]
    public void GetPath_DetachedSubject_ReturnsNull()
    {
        // Arrange
        var detached = new TestContainer(Context) { Name = "Detached" };

        // Act
        var path = Resolver.GetPath(detached);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void GetPath_AfterDetach_ReturnsNull()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        // Verify initial path
        var initialPath = Resolver.GetPath(child);
        Assert.Equal("Child", initialPath);

        // Act - detach
        root.Child = null;
        var pathAfterDetach = Resolver.GetPath(child);

        // Assert
        Assert.Null(pathAfterDetach);
    }

    [Fact]
    public void GetPath_AfterMove_ReturnsNewPath()
    {
        // Arrange
        var child1 = new TestContainer(Context) { Name = "Child1" };
        var child2 = new TestContainer(Context) { Name = "Child2" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child1
        };

        // Verify initial path
        var initialPath = Resolver.GetPath(child1);
        Assert.Equal("Child", initialPath);

        // Act - move child1 to child2, and child2 to root.Child
        child2.Child = child1;
        root.Child = child2;
        var newPath = Resolver.GetPath(child1);

        // Assert
        Assert.Equal("Child.Child", newPath);
    }

    [Fact]
    public void GetPath_CyclicReference_DoesNotInfiniteLoop()
    {
        // Arrange
        var node1 = new TestContainer(Context) { Name = "Node1" };
        var node2 = new TestContainer(Context) { Name = "Node2" };
        var root = new TestContainer(Context) { Name = "Root", Child = node1 };
        node1.Child = node2;
        node2.Child = node1; // Create cycle

        // Act - should not hang
        var path = Resolver.GetPath(node2);

        // Assert - should still return path from root (bracket notation)
        Assert.Equal("Child.Child", path);
    }

    [Fact]
    public void GetPath_SelfReference_DoesNotInfiniteLoop()
    {
        // Arrange
        var node = new TestContainer(Context) { Name = "Node" };
        var root = new TestContainer(Context) { Name = "Root", Child = node };
        node.Child = node; // Self reference

        // Act - should not hang
        var path = Resolver.GetPath(node);

        // Assert
        Assert.Equal("Child", path);
    }

    // NOTE: Multiple parents test skipped - see dictionary limitation note above

    [Fact]
    public void GetPaths_DetachedSubject_ReturnsEmptyList()
    {
        // Arrange
        var detached = new TestContainer(Context) { Name = "Detached" };

        // Act
        var paths = Resolver.GetPaths(detached);

        // Assert
        Assert.Empty(paths);
    }

    [Fact]
    public void GetPaths_RootSubject_ReturnsEmptyList()
    {
        // Arrange - A subject with no parents has no paths from anywhere
        var root = new TestContainer(Context) { Name = "Root" };

        // Act
        var paths = Resolver.GetPaths(root);

        // Assert
        Assert.Empty(paths);
    }

    [Fact]
    public void GetPath_WithInlinePathsAttribute_ReturnsPathWithoutPropertyName()
    {
        // Arrange
        var notes = new TestContainerWithChildren(Context) { Name = "Notes" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainerWithChildren> { ["Notes"] = notes };

        // Act
        var path = Resolver.GetPath(notes);

        // Assert - Should be "Notes" not "Children[Notes]"
        Assert.Equal("Notes", path);
    }

    [Fact]
    public void GetPath_WithInlinePathsAttribute_ReturnsNestedPathWithoutPropertyNames()
    {
        // Arrange
        var page = new TestContainerWithChildren(Context) { Name = "Page" };
        var folder = new TestContainerWithChildren(Context) { Name = "Folder" };
        folder.Children = new Dictionary<string, TestContainerWithChildren> { ["Page"] = page };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainerWithChildren> { ["Folder"] = folder };

        // Act
        var path = Resolver.GetPath(page);

        // Assert - Should be "Folder.Page" not "Children[Folder].Children[Page]"
        Assert.Equal("Folder.Page", path);
    }

    [Fact]
    public void GetPath_WithInlinePathsAttribute_SlashFormat_ReturnsSimplifiedPath()
    {
        // Arrange
        var notes = new TestContainerWithChildren(Context) { Name = "Notes" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainerWithChildren> { ["Notes"] = notes };

        // Act
        var path = Resolver.GetPath(notes, PathFormat.Slash);

        // Assert - Should be "Notes" not "Children/Notes"
        Assert.Equal("Notes", path);
    }

    [Fact]
    public void GetPath_ChildKeyMatchesPropertyName_ReturnsSimplifiedPath_ResolvesToProperty()
    {
        // Arrange - "Child" exists as both property name and dictionary key
        // This test documents the asymmetry when using a dictionary key that matches a property name
        var childProperty = new TestContainerWithChildren(Context) { Name = "Via Property" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Child = childProperty;

        // Act - Get path for the property-based child
        var pathForProperty = Resolver.GetPath(childProperty);

        // Assert - When a child key matches a property name, property takes precedence during resolution
        // This documents that property names should be preferred in path resolution
        Assert.NotNull(pathForProperty);
        Assert.Equal("Child", pathForProperty);

        // When resolving "Child", it resolves to the property, not a hypothetical dictionary entry
        var resolved = Resolver.ResolveSubject(pathForProperty, PathFormat.Bracket, root);
        Assert.Same(childProperty, resolved); // Property wins - documented behavior
    }
}
