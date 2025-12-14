using HomeBlaze.Services.Tests.Models;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class SubjectPathResolverTests
{
    // TODO: Clean up code, fix warnings, etc.

    private readonly IInterceptorSubjectContext _context;
    private readonly SubjectPathResolver _resolver;

    public SubjectPathResolverTests()
    {
        // Create mock dependencies for RootManager
        var mockServiceProvider = new Mock<IServiceProvider>();
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var serializer = new ConfigurableSubjectSerializer(typeRegistry, mockServiceProvider.Object);

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        // Create RootManager with the context (will have null Root)
        var rootManager = new RootManager(typeRegistry, serializer, _context, null);

        // Register RootManager and PathResolver
        _context.WithService(() => rootManager);
        _context.WithPathResolver();

        _resolver = _context.GetService<SubjectPathResolver>();
    }

    // TODO: Maybe instead of regions extract to own test classes?
    
    #region GetPath Tests

    [Fact]
    public void GetPath_RootSubject_ReturnsNull()
    {
        // Arrange
        // A subject with no parents is orphaned/detached - it has no path from anywhere
        var root = new TestContainer(_context) { Name = "Root" };

        // Act
        var path = _resolver.GetPath(root);

        // Assert
        // Even though we call it "root", from the path resolver's perspective,
        // if it has no parents, it's unreachable and has no path
        Assert.Null(path);
    }

    [Fact]
    public void GetPath_DirectChild_ReturnsPropertyName()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Act
        var path = _resolver.GetPath(child);

        // Assert
        Assert.Equal("Child", path);
    }

    [Fact]
    public void GetPath_NestedChild_ReturnsFullPath()
    {
        // Arrange
        var grandchild = new TestContainer(_context) { Name = "Grandchild" };
        var child = new TestContainer(_context)
        {
            Name = "Child",
            Child = grandchild
        };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Act
        var path = _resolver.GetPath(grandchild);

        // Assert - Default format is now bracket notation (Child.Child)
        Assert.Equal("Child.Child", path);
    }

    // NOTE: Dictionary-based path tests are skipped because the interceptor framework
    // doesn't automatically track lifecycle changes when dictionary items are added via indexer.
    // Dictionary modifications need explicit lifecycle management or observable collections.
    // However, ResolveSubject() still works fine with dictionaries (see ResolveSubject tests below).

    [Fact]
    public void GetPath_DetachedSubject_ReturnsNull()
    {
        // Arrange
        var detached = new TestContainer(_context) { Name = "Detached" };

        // Act
        var path = _resolver.GetPath(detached);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void GetPath_AfterDetach_ReturnsNull()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Verify initial path
        var initialPath = _resolver.GetPath(child);
        Assert.Equal("Child", initialPath);

        // Act - detach
        root.Child = null;
        var pathAfterDetach = _resolver.GetPath(child);

        // Assert
        Assert.Null(pathAfterDetach);
    }

    [Fact]
    public void GetPath_AfterMove_ReturnsNewPath()
    {
        // Arrange
        var child1 = new TestContainer(_context) { Name = "Child1" };
        var child2 = new TestContainer(_context) { Name = "Child2" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child1
        };

        // Verify initial path
        var initialPath = _resolver.GetPath(child1);
        Assert.Equal("Child", initialPath);

        // Act - move child1 to child2, and child2 to root.Child
        child2.Child = child1;
        root.Child = child2;
        var newPath = _resolver.GetPath(child1);

        // Assert
        Assert.Equal("Child.Child", newPath);
    }

    [Fact]
    public void GetPath_CyclicReference_DoesNotInfiniteLoop()
    {
        // Arrange
        var node1 = new TestContainer(_context) { Name = "Node1" };
        var node2 = new TestContainer(_context) { Name = "Node2" };
        var root = new TestContainer(_context) { Name = "Root", Child = node1 };
        node1.Child = node2;
        node2.Child = node1; // Create cycle

        // Act - should not hang
        var path = _resolver.GetPath(node2);

        // Assert - should still return path from root (bracket notation)
        Assert.Equal("Child.Child", path);
    }

    [Fact]
    public void GetPath_SelfReference_DoesNotInfiniteLoop()
    {
        // Arrange
        var node = new TestContainer(_context) { Name = "Node" };
        var root = new TestContainer(_context) { Name = "Root", Child = node };
        node.Child = node; // Self reference

        // Act - should not hang
        var path = _resolver.GetPath(node);

        // Assert
        Assert.Equal("Child", path);
    }

    #endregion

    #region GetPaths Tests

    // NOTE: Multiple parents test skipped - see dictionary limitation note above

    [Fact]
    public void GetPaths_DetachedSubject_ReturnsEmptyList()
    {
        // Arrange
        var detached = new TestContainer(_context) { Name = "Detached" };

        // Act
        var paths = _resolver.GetPaths(detached);

        // Assert
        Assert.Empty(paths);
    }

    [Fact]
    public void GetPaths_RootSubject_ReturnsEmptyList()
    {
        // Arrange
        // A subject with no parents has no paths from anywhere
        var root = new TestContainer(_context) { Name = "Root" };

        // Act
        var paths = _resolver.GetPaths(root);

        // Assert
        Assert.Empty(paths);
    }

    #endregion

    #region ResolveSubject Tests

    [Fact]
    public void ResolveSubject_EmptyPath_ReturnsRoot()
    {
        // Arrange
        var root = new TestContainer(_context) { Name = "Root" };

        // Act
        var result = _resolver.ResolveSubject("", PathFormat.Slash, root);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_NullPath_ReturnsRoot()
    {
        // Arrange
        var root = new TestContainer(_context) { Name = "Root" };

        // Act
        var result = _resolver.ResolveSubject(null!, PathFormat.Slash, root);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_ValidPath_ReturnsSubject()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Act
        var result = _resolver.ResolveSubject("Child", PathFormat.Slash, root);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_InvalidPath_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(_context) { Name = "Root" };

        // Act
        var result = _resolver.ResolveSubject("NonExistent", PathFormat.Slash, root);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_DictionaryPath_ReturnsSubject()
    {
        // Arrange
        var notes = new TestContainer(_context) { Name = "Notes" };
        var root = new TestContainer(_context) { Name = "Root" };
        root.Children["Notes"] = notes;

        // Act
        var result = _resolver.ResolveSubject("Children/Notes", PathFormat.Slash, root);

        // Assert
        Assert.Same(notes, result);
    }

    [Fact]
    public void ResolveSubject_NestedPath_ReturnsDeepChild()
    {
        // Arrange
        var deepChild = new TestContainer(_context) { Name = "DeepChild" };
        var notes = new TestContainer(_context) { Name = "Notes", Child = deepChild };
        var root = new TestContainer(_context) { Name = "Root" };
        root.Children["Notes"] = notes;

        // Act
        var result = _resolver.ResolveSubject("Children/Notes/Child", PathFormat.Slash, root);

        // Assert
        Assert.Same(deepChild, result);
    }

    [Fact]
    public void ResolveSubject_UrlEncodedPath_DecodesSegments()
    {
        // Arrange
        var file = new TestContainer(_context) { Name = "My File.md" };
        var root = new TestContainer(_context) { Name = "Root" };
        root.Children["My File.md"] = file;

        // Act
        var result = _resolver.ResolveSubject("Children/My%20File.md", PathFormat.Slash, root);

        // Assert
        Assert.Same(file, result);
    }

    [Fact]
    public void ResolveSubject_WithRootAlone_ReturnsRootSubject()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act - "Root" should return the root subject
        var result = _resolver.ResolveSubject("Root");

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_WithRootDotPrefix_ResolvesFromRoot()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act - "Root.Child" should resolve to the child
        var result = _resolver.ResolveSubject("Root.Child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_WithRootDotPrefix_CaseSensitive_LowercaseDoesNotMatch()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act - "root" (lowercase) should not match, returns null
        var result = _resolver.ResolveSubject("root");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_WithRootDotPrefix_CaseSensitive_LowercasePrefixDoesNotMatch()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act - "root.Child" (lowercase) should not match
        var result = _resolver.ResolveSubject("root.Child");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_WithThisRootPropertyPath_ResolvesCorrectly()
    {
        // Arrange - if a subject has a property/child named "Root",
        // the user can access it via "this.Root" to disambiguate
        // (though TestContainer doesn't have a "Root" property, this tests the pattern)
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act - resolve child relative to root, accessing Child property
        var result = _resolver.ResolveSubject("Child", root: root);

        // Assert
        Assert.Same(child, result);
    }

    #endregion

    #region Cache Invalidation Tests

    [Fact]
    public void GetPath_CacheInvalidatedOnDetach_ReturnsUpdatedPath()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context) { Name = "Root", Child = child };

        // Get initial path (caches it)
        var path1 = _resolver.GetPath(child);
        Assert.Equal("Child", path1);

        // Act - detach and get again
        root.Child = null;
        var path2 = _resolver.GetPath(child);

        // Assert
        Assert.Null(path2);
    }

    [Fact]
    public void GetPath_CacheInvalidatedOnAttach_ReturnsUpdatedPath()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };

        // Get initial path for detached subject (caches null)
        var path1 = _resolver.GetPath(child);
        Assert.Null(path1);

        // Act - attach and get again
        var root = new TestContainer(_context) { Name = "Root", Child = child };
        var path2 = _resolver.GetPath(child);

        // Assert
        Assert.Equal("Child", path2);
    }

    #endregion

    #region ResolveFromRelativePath Tests

    [Fact]
    public void ResolveFromRelativePath_WithRootAlone_ReturnsRoot()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Register root with RootManager
        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act - just "Root" should return the root subject
        var result = _resolver.ResolveFromRelativePath("Root");

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveFromRelativePath_WithRootPrefix_ResolvesFromRoot()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Register root with RootManager
        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act
        var result = _resolver.ResolveFromRelativePath("Root.Child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveFromRelativePath_WithRootPrefix_NoCurrentSubject_StillWorks()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act - no current subject provided
        var result = _resolver.ResolveFromRelativePath("Root.Child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveFromRelativePath_WithThisPrefix_ResolvesFromCurrentSubject()
    {
        // Arrange
        var grandchild = new TestContainer(_context) { Name = "Grandchild" };
        var child = new TestContainer(_context)
        {
            Name = "Child",
            Child = grandchild
        };

        // Act - "this.Child" from child should resolve grandchild
        var result = _resolver.ResolveFromRelativePath("this.Child", child);

        // Assert
        Assert.Same(grandchild, result);
    }

    [Fact]
    public void ResolveFromRelativePath_WithThisPrefix_NoCurrentSubject_ReturnsNull()
    {
        // Act
        var result = _resolver.ResolveFromRelativePath("this.Child");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveFromRelativePath_SingleParentNav_ResolvesToParent()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Act - "../" from child should resolve to root
        var result = _resolver.ResolveFromRelativePath("../", child);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveFromRelativePath_MultipleParentNav_TraversesUpMultipleLevels()
    {
        // Arrange
        var grandchild = new TestContainer(_context) { Name = "Grandchild" };
        var child = new TestContainer(_context)
        {
            Name = "Child",
            Child = grandchild
        };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Act - "../../" from grandchild should resolve to root
        var result = _resolver.ResolveFromRelativePath("../../", grandchild);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveFromRelativePath_ParentNavWithPath_ResolvesFromParent()
    {
        // Arrange
        // Note: Using Child property instead of dictionary because dictionary-based
        // lifecycle tracking doesn't work automatically (see note earlier in this file)
        var grandchild = new TestContainer(_context) { Name = "Grandchild" };
        var child = new TestContainer(_context)
        {
            Name = "Child",
            Child = grandchild
        };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Act - "../Child" from child should resolve to grandchild (go up to root, then down to Child.Child)
        // But first let's test a simpler case: from grandchild, go up and access Child property of root
        var result = _resolver.ResolveFromRelativePath("../../Child", grandchild);

        // Assert - should get child (root.Child)
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveFromRelativePath_ParentNavNoParent_ReturnsNull()
    {
        // Arrange
        var detached = new TestContainer(_context) { Name = "Detached" };

        // Act - "../" from detached subject with no parent
        var result = _resolver.ResolveFromRelativePath("../", detached);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveFromRelativePath_NoPrefix_ResolvesFromCurrentSubject()
    {
        // Arrange
        var grandchild = new TestContainer(_context) { Name = "Grandchild" };
        var child = new TestContainer(_context)
        {
            Name = "Child",
            Child = grandchild
        };

        // Act - "Child" from child should resolve grandchild
        var result = _resolver.ResolveFromRelativePath("Child", child);

        // Assert
        Assert.Same(grandchild, result);
    }

    [Fact]
    public void ResolveFromRelativePath_NoPrefix_NoCurrentSubject_ReturnsNull()
    {
        // Act
        var result = _resolver.ResolveFromRelativePath("Child");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveFromRelativePath_EmptyPath_ReturnsCurrentSubject()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };

        // Act
        var result = _resolver.ResolveFromRelativePath("", child);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveFromRelativePath_NullPath_ReturnsCurrentSubject()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "Child" };

        // Act
        var result = _resolver.ResolveFromRelativePath(null!, child);

        // Assert
        Assert.Same(child, result);
    }

    #endregion

    #region ResolveValueFromRelativePath Tests

    [Fact]
    public void ResolveValueFromRelativePath_WithRootPrefix_ResolvesPropertyValue()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "TestChild" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        var rootManager = _context.GetService<RootManager>();
        rootManager.Root = root;

        // Act - "Root.Child.Name" should resolve to "TestChild"
        var result = _resolver.ResolveValueFromRelativePath("Root.Child.Name", null);

        // Assert
        Assert.Equal("TestChild", result);
    }

    [Fact]
    public void ResolveValueFromRelativePath_WithInlineSubjects_LooksUpByKey()
    {
        // Arrange
        var motor = new TestContainer(_context) { Name = "Motor1" };
        var inlineSubjects = new Dictionary<string, IInterceptorSubject>
        {
            ["motor"] = motor
        };

        // Act - "motor.Name" should look up motor in inline subjects then get Name property
        var result = _resolver.ResolveValueFromRelativePath("motor.Name", null, inlineSubjects);

        // Assert
        Assert.Equal("Motor1", result);
    }

    [Fact]
    public void ResolveValueFromRelativePath_WithInlineSubjects_KeyNotFound_FallsBackToRelativePath()
    {
        // Arrange
        var current = new TestContainer(_context) { Name = "Current" };
        var child = new TestContainer(_context) { Name = "ChildName" };
        current.Child = child;

        var inlineSubjects = new Dictionary<string, IInterceptorSubject>
        {
            ["other"] = new TestContainer(_context) { Name = "Other" }
        };

        // Act - "Child.Name" should fall back to relative path since "Child" is not in inlineSubjects
        var result = _resolver.ResolveValueFromRelativePath("Child.Name", current, inlineSubjects);

        // Assert
        Assert.Equal("ChildName", result);
    }

    [Fact]
    public void ResolveValueFromRelativePath_RelativePath_ResolvesFromCurrentSubject()
    {
        // Arrange
        var child = new TestContainer(_context) { Name = "ChildValue" };
        var current = new TestContainer(_context) { Name = "Current", Child = child };

        // Act - "Child.Name" from current
        var result = _resolver.ResolveValueFromRelativePath("Child.Name", current);

        // Assert
        Assert.Equal("ChildValue", result);
    }

    [Fact]
    public void ResolveValueFromRelativePath_EmptyPath_ReturnsNull()
    {
        // Arrange
        var current = new TestContainer(_context) { Name = "Current" };

        // Act
        var result = _resolver.ResolveValueFromRelativePath("", current);

        // Assert
        Assert.Null(result);
    }

    #endregion
}
