using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver.ResolveSubject() method.
/// </summary>
public class SubjectPathResolverResolveSubjectTests : SubjectPathResolverTestBase
{
    [Fact]
    public void ResolveSubject_EmptyPath_ReturnsRoot()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };

        // Act
        var result = Resolver.ResolveSubject("", PathFormat.Slash, root);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_NullPath_ReturnsRoot()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };

        // Act
        var result = Resolver.ResolveSubject(null!, PathFormat.Slash, root);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_ValidPath_ReturnsSubject()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        // Act
        var result = Resolver.ResolveSubject("Child", PathFormat.Slash, root);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_InvalidPath_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };

        // Act
        var result = Resolver.ResolveSubject("NonExistent", PathFormat.Slash, root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_DictionaryPath_ReturnsSubject()
    {
        // Arrange
        var notes = new TestContainer(Context) { Name = "Notes" };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children["Notes"] = notes;

        // Act
        var result = Resolver.ResolveSubject("Children/Notes", PathFormat.Slash, root);

        // Assert
        Assert.Same(notes, result);
    }

    [Fact]
    public void ResolveSubject_NestedPath_ReturnsDeepChild()
    {
        // Arrange
        var deepChild = new TestContainer(Context) { Name = "DeepChild" };
        var notes = new TestContainer(Context) { Name = "Notes", Child = deepChild };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children["Notes"] = notes;

        // Act
        var result = Resolver.ResolveSubject("Children/Notes/Child", PathFormat.Slash, root);

        // Assert
        Assert.Same(deepChild, result);
    }

    [Fact]
    public void ResolveSubject_UrlEncodedPath_DecodesSegments()
    {
        // Arrange
        var file = new TestContainer(Context) { Name = "My File.md" };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children["My File.md"] = file;

        // Act
        var result = Resolver.ResolveSubject("Children/My%20File.md", PathFormat.Slash, root);

        // Assert
        Assert.Same(file, result);
    }

    [Fact]
    public void ResolveSubject_WithRootAlone_ReturnsRootSubject()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act - "Root" should return the root subject
        var result = Resolver.ResolveSubject("Root");

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_WithRootDotPrefix_ResolvesFromRoot()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act - "Root.Child" should resolve to the child
        var result = Resolver.ResolveSubject("Root.Child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_WithRootDotPrefix_CaseSensitive_LowercaseDoesNotMatch()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act - "root" (lowercase) should not match, returns null
        var result = Resolver.ResolveSubject("root");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_WithRootDotPrefix_CaseSensitive_LowercasePrefixDoesNotMatch()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act - "root.Child" (lowercase) should not match
        var result = Resolver.ResolveSubject("root.Child");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_WithThisRootPropertyPath_ResolvesCorrectly()
    {
        // Arrange - if a subject has a property/child named "Root",
        // the user can access it via "this.Root" to disambiguate
        // (though TestContainer doesn't have a "Root" property, this tests the pattern)
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act - resolve child relative to root, accessing Child property
        var result = Resolver.ResolveSubject("Child", root: root);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_WithChildrenAttribute_ResolvesWithoutPropertyName()
    {
        // Arrange
        var notes = new TestContainerWithChildren(Context) { Name = "Notes" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children["Notes"] = notes;

        // Act - "Notes" should resolve via [Children] property without "Children/" prefix
        var result = Resolver.ResolveSubject("Notes", PathFormat.Slash, root);

        // Assert
        Assert.Same(notes, result);
    }

    [Fact]
    public void ResolveSubject_WithChildrenAttribute_ResolvesNestedPath()
    {
        // Arrange
        var page = new TestContainerWithChildren(Context) { Name = "Page" };
        var folder = new TestContainerWithChildren(Context) { Name = "Folder" };
        folder.Children["Page"] = page;
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children["Folder"] = folder;

        // Act - "Folder/Page" should resolve through nested [Children]
        var result = Resolver.ResolveSubject("Folder/Page", PathFormat.Slash, root);

        // Assert
        Assert.Same(page, result);
    }

    [Fact]
    public void ResolveSubject_WithChildrenAttribute_PropertyTakesPrecedenceOverChildKey()
    {
        // Arrange
        var childProperty = new TestContainerWithChildren(Context) { Name = "ChildProperty" };
        var childInDictionary = new TestContainerWithChildren(Context) { Name = "ChildInDictionary" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Child = childProperty;
        root.Children["Child"] = childInDictionary; // Same name as property

        // Act - "Child" should resolve to the property, not the dictionary entry
        var result = Resolver.ResolveSubject("Child", PathFormat.Slash, root);

        // Assert - Property takes precedence
        Assert.Same(childProperty, result);
    }

    [Fact]
    public void ResolveSubject_WithChildrenAttribute_ExplicitBracketSyntaxStillWorks()
    {
        // Arrange
        var notes = new TestContainerWithChildren(Context) { Name = "Notes" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children["Notes"] = notes;

        // Act - Explicit "Children[Notes]" should still work
        var result = Resolver.ResolveSubject("Children[Notes]", PathFormat.Bracket, root);

        // Assert
        Assert.Same(notes, result);
    }
}
