using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver.GetPath() and GetPaths() methods.
/// </summary>
public class SubjectPathResolverGetPathTests : SubjectPathResolverTestBase
{
    #region Canonical paths

    [Fact]
    public void GetPath_RootSubject_ReturnsSlash()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(root, PathStyle.Canonical);

        // Assert
        Assert.Equal("/", path);
    }

    [Fact]
    public void GetPath_DetachedSubject_ReturnsNull()
    {
        // Arrange
        var detached = new TestContainer(Context) { Name = "Detached" };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(detached, PathStyle.Canonical);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void GetPath_DirectChild_ReturnsSlashPropertyName()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(child, PathStyle.Canonical);

        // Assert
        Assert.Equal("/Child", path);
    }

    [Fact]
    public void GetPath_NestedChild_ReturnsFullPath()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(grandchild, PathStyle.Canonical);

        // Assert
        Assert.Equal("/Child/Child", path);
    }

    [Fact]
    public void GetPath_AfterDetach_ReturnsNull()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Verify initial path
        var initialPath = Resolver.GetPath(child, PathStyle.Canonical);
        Assert.Equal("/Child", initialPath);

        // Act - detach
        root.Child = null;
        var pathAfterDetach = Resolver.GetPath(child, PathStyle.Canonical);

        // Assert
        Assert.Null(pathAfterDetach);
    }

    [Fact]
    public void GetPath_AfterMove_ReturnsNewPath()
    {
        // Arrange
        var child1 = new TestContainer(Context) { Name = "Child1" };
        var child2 = new TestContainer(Context) { Name = "Child2" };
        var root = new TestContainer(Context) { Name = "Root", Child = child1 };
        RootManager.Root = root;

        // Verify initial path
        var initialPath = Resolver.GetPath(child1, PathStyle.Canonical);
        Assert.Equal("/Child", initialPath);

        // Act - move child1 under child2, and child2 to root.Child
        child2.Child = child1;
        root.Child = child2;
        var newPath = Resolver.GetPath(child1, PathStyle.Canonical);

        // Assert
        Assert.Equal("/Child/Child", newPath);
    }

    [Fact]
    public void GetPath_CyclicReference_DoesNotHang()
    {
        // Arrange
        var node1 = new TestContainer(Context) { Name = "Node1" };
        var node2 = new TestContainer(Context) { Name = "Node2" };
        var root = new TestContainer(Context) { Name = "Root", Child = node1 };
        RootManager.Root = root;
        node1.Child = node2;
        node2.Child = node1; // Create cycle

        // Act - should not hang
        var path = Resolver.GetPath(node2, PathStyle.Canonical);

        // Assert - should still return path from root
        Assert.Equal("/Child/Child", path);
    }

    [Fact]
    public void GetPath_SelfReference_DoesNotHang()
    {
        // Arrange
        var node = new TestContainer(Context) { Name = "Node" };
        var root = new TestContainer(Context) { Name = "Root", Child = node };
        RootManager.Root = root;
        node.Child = node; // Self reference

        // Act - should not hang
        var path = Resolver.GetPath(node, PathStyle.Canonical);

        // Assert
        Assert.Equal("/Child", path);
    }

    [Fact]
    public void GetPaths_DetachedSubject_ReturnsEmpty()
    {
        // Arrange
        var detached = new TestContainer(Context) { Name = "Detached" };
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var paths = Resolver.GetPaths(detached, PathStyle.Canonical);

        // Assert
        Assert.Empty(paths);
    }

    [Fact]
    public void GetPath_InlinePaths_ReturnsPathWithoutPropertyName()
    {
        // Arrange
        var notes = new TestContainerWithChildren(Context) { Name = "Notes" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainerWithChildren> { ["Notes"] = notes };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(notes, PathStyle.Canonical);

        // Assert - Should be "/Notes" not "/Children[Notes]"
        Assert.Equal("/Notes", path);
    }

    [Fact]
    public void GetPath_InlinePaths_Nested_ReturnsFullPath()
    {
        // Arrange
        var page = new TestContainerWithChildren(Context) { Name = "Page" };
        var folder = new TestContainerWithChildren(Context) { Name = "Folder" };
        folder.Children = new Dictionary<string, TestContainerWithChildren> { ["Page"] = page };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainerWithChildren> { ["Folder"] = folder };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(page, PathStyle.Canonical);

        // Assert - Should be "/Folder/Page" not "/Children[Folder]/Children[Page]"
        Assert.Equal("/Folder/Page", path);
    }

    [Fact]
    public void GetPath_InlinePaths_KeyWithDot_ReturnsPathWithDot()
    {
        // Arrange
        var setupMd = new TestContainerWithChildren(Context) { Name = "Setup.md" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainerWithChildren> { ["Setup.md"] = setupMd };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(setupMd, PathStyle.Canonical);

        // Assert - Dots are fine in keys with slash-based paths (no brackets needed)
        Assert.Equal("/Setup.md", path);
    }

    [Fact]
    public void GetPath_PropertyTakesPrecedenceOverInlinePathsKey()
    {
        // Arrange - "Child" exists as both property name and dictionary key
        var childProperty = new TestContainerWithChildren(Context) { Name = "Via Property" };
        var childInDict = new TestContainerWithChildren(Context) { Name = "Via Dictionary" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Child = childProperty;
        root.Children = new Dictionary<string, TestContainerWithChildren> { ["Child"] = childInDict };
        RootManager.Root = root;

        // Act - Get path for the property-based child
        var pathForProperty = Resolver.GetPath(childProperty, PathStyle.Canonical);

        // Assert - Property path is /Child
        Assert.Equal("/Child", pathForProperty);

        // Resolving /Child should give the property, not the dictionary entry
        var resolved = Resolver.ResolveSubject("/Child", PathStyle.Canonical);
        Assert.Same(childProperty, resolved);
    }

    #endregion

    #region Route paths

    [Fact]
    public void GetPath_Route_SimplePath_SameAsCanonical()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(child, PathStyle.Route);

        // Assert - simple paths are identical across styles
        Assert.Equal("/Child", path);
    }

    [Fact]
    public void GetPath_Route_InlinePaths_SameAsCanonical()
    {
        // Arrange
        var notes = new TestContainerWithChildren(Context) { Name = "Notes" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children = new Dictionary<string, TestContainerWithChildren> { ["Notes"] = notes };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(notes, PathStyle.Route);

        // Assert - InlinePaths are the same for both styles
        Assert.Equal("/Notes", path);
    }

    #endregion

    #region CanonicalToRoute conversion

    [Theory]
    [InlineData("/Child", "/Child")]
    [InlineData("/Items[0]/Name", "/Items/0/Name")]
    [InlineData("/Children[Notes]/Child", "/Children/Notes/Child")]
    [InlineData("/", "/")]
    public void CanonicalToRoute_ConvertsCorrectly(string canonical, string expected)
    {
        var route = SubjectPathResolver.CanonicalToRoute(canonical);
        Assert.Equal(expected, route);
    }

    #endregion
}
