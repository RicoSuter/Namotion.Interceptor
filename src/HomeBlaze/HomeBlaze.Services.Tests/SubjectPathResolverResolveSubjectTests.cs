using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver.ResolveSubject() method.
/// </summary>
public class SubjectPathResolverResolveSubjectTests : SubjectPathResolverTestBase
{
    #region Absolute paths

    [Fact]
    public void ResolveSubject_Slash_ReturnsRoot()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/", PathStyle.Canonical);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_AbsoluteChild_ReturnsChild()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/Child", PathStyle.Canonical);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_AbsoluteNonExistent_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/NonExistent", PathStyle.Canonical);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_AbsolutePath_IgnoresRelativeTo()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        var unrelated = new TestContainer(Context) { Name = "Unrelated" };

        // Act - absolute path should ignore relativeTo
        var result = Resolver.ResolveSubject("/Child", PathStyle.Canonical, unrelated);

        // Assert
        Assert.Same(child, result);
    }

    #endregion

    #region Empty/null paths

    [Fact]
    public void ResolveSubject_EmptyPath_ReturnsRelativeTo()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("", PathStyle.Canonical, child);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_NullPath_ReturnsRelativeTo()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject(null!, PathStyle.Canonical, child);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_EmptyPath_NoRelativeTo_ReturnsRoot()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("", PathStyle.Canonical);

        // Assert
        Assert.Same(root, result);
    }

    #endregion

    #region No-prefix (implicit relative)

    [Fact]
    public void ResolveSubject_NoPrefix_ResolvesRelativeToRelativeTo()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - "Child" relative to child resolves grandchild
        var result = Resolver.ResolveSubject("Child", PathStyle.Canonical, child);

        // Assert
        Assert.Same(grandchild, result);
    }

    [Fact]
    public void ResolveSubject_NoPrefix_NoRelativeTo_ResolvesFromRoot()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - no relativeTo falls back to root
        var result = Resolver.ResolveSubject("Child", PathStyle.Canonical);

        // Assert
        Assert.Same(child, result);
    }

    #endregion

    #region Explicit relative (./)

    [Fact]
    public void ResolveSubject_DotSlashChild_ResolvesRelative()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("./Child", PathStyle.Canonical, child);

        // Assert
        Assert.Same(grandchild, result);
    }

    [Fact]
    public void ResolveSubject_DotSlashAlone_ReturnsSelf()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - "./" alone returns relativeTo itself
        var result = Resolver.ResolveSubject("./", PathStyle.Canonical, child);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_DotSlashAlone_NoRelativeTo_FallsBackToRoot()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act - "./" with no relativeTo falls back to root
        var result = Resolver.ResolveSubject("./", PathStyle.Canonical);

        // Assert
        Assert.Same(root, result);
    }

    #endregion

    #region Parent navigation (../)

    [Fact]
    public void ResolveSubject_DotDotSlash_ReturnsParent()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("../", PathStyle.Canonical, child);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_DoubleDotDotSlash_ReturnsGrandparent()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - "../../" from grandchild should resolve to root
        var result = Resolver.ResolveSubject("../../", PathStyle.Canonical, grandchild);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_DotDotSlashThenResolve_NavigatesUpThenDown()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - from grandchild, go up twice and access Child property of root
        var result = Resolver.ResolveSubject("../../Child", PathStyle.Canonical, grandchild);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_DotDotSlash_NoParent_ReturnsNull()
    {
        // Arrange
        var detached = new TestContainer(Context) { Name = "Detached" };

        // Act - "../" from subject with no parent
        var result = Resolver.ResolveSubject("../", PathStyle.Canonical, detached);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_DotDotSlash_NoRelativeTo_ReturnsNull()
    {
        // Act
        var result = Resolver.ResolveSubject("../", PathStyle.Canonical);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Dictionary paths (Canonical and Route)

    [Fact]
    public void ResolveSubject_CanonicalDictionaryPath_ResolvesBracketNotation()
    {
        // Arrange
        var notes = new TestContainer(Context) { Name = "Notes" };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children["Notes"] = notes;
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/Children[Notes]", PathStyle.Canonical);

        // Assert
        Assert.Same(notes, result);
    }

    [Fact]
    public void ResolveSubject_RouteDictionaryPath_ResolvesSlashNotation()
    {
        // Arrange
        var notes = new TestContainer(Context) { Name = "Notes" };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children["Notes"] = notes;
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/Children/Notes", PathStyle.Route);

        // Assert
        Assert.Same(notes, result);
    }

    [Fact]
    public void ResolveSubject_CanonicalNestedDictionary_ResolvesDeepPath()
    {
        // Arrange
        var deepChild = new TestContainer(Context) { Name = "DeepChild" };
        var notes = new TestContainer(Context) { Name = "Notes", Child = deepChild };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children["Notes"] = notes;
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/Children[Notes]/Child", PathStyle.Canonical);

        // Assert
        Assert.Same(deepChild, result);
    }

    [Fact]
    public void ResolveSubject_UrlEncoded_DecodesSegments()
    {
        // Arrange
        var file = new TestContainer(Context) { Name = "My File.md" };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children["My File.md"] = file;
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/Children[My%20File.md]", PathStyle.Canonical);

        // Assert
        Assert.Same(file, result);
    }

    [Theory]
    [InlineData("/Items/3", PathStyle.Route)]
    [InlineData("/Items[3]", PathStyle.Canonical)]
    public void WhenDictionaryHasIntKeys_ThenResolveSubjectFindsEntryByKeyText(string path, PathStyle style)
    {
        // Arrange
        var item = new TestContainer(Context) { Name = "Item" };
        var root = new TestContainerWithKeyedChildren(Context);
        root.Items = new Dictionary<int, TestContainer> { [3] = item };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject(path, style);

        // Assert
        Assert.Same(item, result);
    }

    [Fact]
    public void WhenDictionaryHasGuidKeys_ThenResolveSubjectFindsEntryByKeyText()
    {
        // Arrange
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var item = new TestContainer(Context) { Name = "Item" };
        var root = new TestContainerWithKeyedChildren(Context);
        root.ItemsById = new Dictionary<Guid, TestContainer> { [id] = item };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/ItemsById/0f8fad5b-d9cb-469f-a165-70867728950e", PathStyle.Route);

        // Assert
        Assert.Same(item, result);
    }

    [Fact]
    public void WhenDictionaryHasNoEntryForIntKeyText_ThenResolveSubjectReturnsNull()
    {
        // Arrange
        var root = new TestContainerWithKeyedChildren(Context);
        root.Items = new Dictionary<int, TestContainer> { [3] = new TestContainer(Context) { Name = "Item" } };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/Items/4", PathStyle.Route);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(PathStyle.Route)]
    [InlineData(PathStyle.Canonical)]
    public void WhenWrittenPathOfIntKeyedEntryIsResolved_ThenReturnsEntry(PathStyle style)
    {
        // Arrange
        var item = new TestContainer(Context) { Name = "Item" };
        var root = new TestContainerWithKeyedChildren(Context);
        root.Items = new Dictionary<int, TestContainer> { [3] = item };
        RootManager.Root = root;
        var path = Resolver.GetPath(item, style)!;

        // Act
        var result = Resolver.ResolveSubject(path, style);

        // Assert
        Assert.Same(item, result);
    }

    [Theory]
    [InlineData(PathStyle.Route, "/Items/-3")]
    [InlineData(PathStyle.Canonical, "/Items[-3]")]
    public void WhenCultureWritesNegativeNumbersWithAnotherSign_ThenWrittenPathHasInvariantKeyAndResolves(PathStyle style, string expectedPath)
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // Swedish writes a negative number with U+2212, not with the ASCII hyphen-minus of the invariant culture.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            Assert.Equal("−", CultureInfo.CurrentCulture.NumberFormat.NegativeSign);

            var item = new TestContainer(Context) { Name = "Item" };
            var root = new TestContainerWithKeyedChildren(Context);
            root.Items = new Dictionary<int, TestContainer> { [-3] = item };
            RootManager.Root = root;

            // Act
            var path = Resolver.GetPath(item, style);
            var result = Resolver.ResolveSubject(path!, style);

            // Assert
            Assert.Equal(expectedPath, path);
            Assert.Same(item, result);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    #endregion

    #region InlinePaths resolution

    [Theory]
    [InlineData(PathStyle.Route)]
    [InlineData(PathStyle.Canonical)]
    public void WhenInlinePathsDictionaryHasIntKeys_ThenResolveSubjectFindsEntryByKeyText(PathStyle style)
    {
        // Arrange
        var item = new TestContainer(Context) { Name = "Item" };
        var root = new TestContainerWithIntInlinePaths(Context);
        root.Items = new Dictionary<int, TestContainer> { [3] = item };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveSubject("/3", style);

        // Assert
        Assert.Same(item, result);
    }

    [Theory]
    [InlineData(PathStyle.Route)]
    [InlineData(PathStyle.Canonical)]
    public void WhenWrittenPathOfIntKeyedInlinePathsEntryIsResolved_ThenReturnsEntry(PathStyle style)
    {
        // Arrange
        var item = new TestContainer(Context) { Name = "Item" };
        var root = new TestContainerWithIntInlinePaths(Context);
        root.Items = new Dictionary<int, TestContainer> { [3] = item };
        RootManager.Root = root;

        // Act
        var path = Resolver.GetPath(item, style);
        var result = Resolver.ResolveSubject(path!, style);

        // Assert
        Assert.Equal("/3", path);
        Assert.Same(item, result);
    }

    [Fact]
    public void ResolveSubject_InlinePaths_ResolvesWithoutPropertyName()
    {
        // Arrange
        var notes = new TestContainerWithChildren(Context) { Name = "Notes" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children["Notes"] = notes;
        RootManager.Root = root;

        // Act - "/Notes" should resolve via [InlinePaths] without needing "Children/" prefix
        var result = Resolver.ResolveSubject("/Notes", PathStyle.Canonical);

        // Assert
        Assert.Same(notes, result);
    }

    [Fact]
    public void ResolveSubject_InlinePaths_Nested_ResolvesDeepPath()
    {
        // Arrange
        var page = new TestContainerWithChildren(Context) { Name = "Page" };
        var folder = new TestContainerWithChildren(Context) { Name = "Folder" };
        folder.Children["Page"] = page;
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children["Folder"] = folder;
        RootManager.Root = root;

        // Act - "/Folder/Page" should resolve through nested [InlinePaths]
        var result = Resolver.ResolveSubject("/Folder/Page", PathStyle.Canonical);

        // Assert
        Assert.Same(page, result);
    }

    [Fact]
    public void ResolveSubject_InlinePaths_PropertyTakesPrecedenceOverKey()
    {
        // Arrange
        var childProperty = new TestContainerWithChildren(Context) { Name = "ChildProperty" };
        var childInDict = new TestContainerWithChildren(Context) { Name = "ChildInDictionary" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Child = childProperty;
        root.Children["Child"] = childInDict; // Same name as the Child property

        RootManager.Root = root;

        // Act - "/Child" should resolve to the property, not the dictionary entry
        var result = Resolver.ResolveSubject("/Child", PathStyle.Canonical);

        // Assert - Property takes precedence
        Assert.Same(childProperty, result);
    }

    #endregion
}
