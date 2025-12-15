using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver.ResolveFromRelativePath() and ResolveValueFromRelativePath() methods.
/// </summary>
public class SubjectPathResolverRelativePathTests : SubjectPathResolverTestBase
{
    #region ResolveFromRelativePath Tests

    [Fact]
    public void ResolveFromRelativePath_WithRootAlone_ReturnsRoot()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act - just "Root" should return the root subject
        var result = Resolver.ResolveFromRelativePath("Root");

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveFromRelativePath_WithRootPrefix_ResolvesFromRoot()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveFromRelativePath("Root.Child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveFromRelativePath_WithRootPrefix_NoCurrentSubject_StillWorks()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act - no current subject provided
        var result = Resolver.ResolveFromRelativePath("Root.Child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveFromRelativePath_WithThisPrefix_ResolvesFromCurrentSubject()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context)
        {
            Name = "Child",
            Child = grandchild
        };

        // Act - "this.Child" from child should resolve grandchild
        var result = Resolver.ResolveFromRelativePath("this.Child", child);

        // Assert
        Assert.Same(grandchild, result);
    }

    [Fact]
    public void ResolveFromRelativePath_WithThisPrefix_NoCurrentSubject_ReturnsNull()
    {
        // Act
        var result = Resolver.ResolveFromRelativePath("this.Child");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveFromRelativePath_SingleParentNav_ResolvesToParent()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        // Act - "../" from child should resolve to root
        var result = Resolver.ResolveFromRelativePath("../", child);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveFromRelativePath_MultipleParentNav_TraversesUpMultipleLevels()
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

        // Act - "../../" from grandchild should resolve to root
        var result = Resolver.ResolveFromRelativePath("../../", grandchild);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveFromRelativePath_ParentNavWithPath_ResolvesFromParent()
    {
        // Arrange
        // Note: Using Child property instead of dictionary because dictionary-based
        // lifecycle tracking doesn't work automatically
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

        // Act - from grandchild, go up twice and access Child property of root
        var result = Resolver.ResolveFromRelativePath("../../Child", grandchild);

        // Assert - should get child (root.Child)
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveFromRelativePath_ParentNavNoParent_ReturnsNull()
    {
        // Arrange
        var detached = new TestContainer(Context) { Name = "Detached" };

        // Act - "../" from detached subject with no parent
        var result = Resolver.ResolveFromRelativePath("../", detached);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveFromRelativePath_NoPrefix_ResolvesFromCurrentSubject()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context)
        {
            Name = "Child",
            Child = grandchild
        };

        // Act - "Child" from child should resolve grandchild
        var result = Resolver.ResolveFromRelativePath("Child", child);

        // Assert
        Assert.Same(grandchild, result);
    }

    [Fact]
    public void ResolveFromRelativePath_NoPrefix_NoCurrentSubject_ReturnsNull()
    {
        // Act
        var result = Resolver.ResolveFromRelativePath("Child");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveFromRelativePath_EmptyPath_ReturnsCurrentSubject()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };

        // Act
        var result = Resolver.ResolveFromRelativePath("", child);

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveFromRelativePath_NullPath_ReturnsCurrentSubject()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "Child" };

        // Act
        var result = Resolver.ResolveFromRelativePath(null!, child);

        // Assert
        Assert.Same(child, result);
    }

    #endregion

    #region ResolveValueFromRelativePath Tests

    [Fact]
    public void ResolveValueFromRelativePath_WithRootPrefix_ResolvesPropertyValue()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "TestChild" };
        var root = new TestContainer(Context)
        {
            Name = "Root",
            Child = child
        };

        RootManager.Root = root;

        // Act - "Root.Child.Name" should resolve to "TestChild"
        var result = Resolver.ResolveValueFromRelativePath("Root.Child.Name", null);

        // Assert
        Assert.Equal("TestChild", result);
    }

    [Fact]
    public void ResolveValueFromRelativePath_WithInlineSubjects_LooksUpByKey()
    {
        // Arrange
        var motor = new TestContainer(Context) { Name = "Motor1" };
        var inlineSubjects = new Dictionary<string, IInterceptorSubject>
        {
            ["motor"] = motor
        };

        // Act - "motor.Name" should look up motor in inline subjects then get Name property
        var result = Resolver.ResolveValueFromRelativePath("motor.Name", null, inlineSubjects);

        // Assert
        Assert.Equal("Motor1", result);
    }

    [Fact]
    public void ResolveValueFromRelativePath_WithInlineSubjects_KeyNotFound_FallsBackToRelativePath()
    {
        // Arrange
        var current = new TestContainer(Context) { Name = "Current" };
        var child = new TestContainer(Context) { Name = "ChildName" };
        current.Child = child;

        var inlineSubjects = new Dictionary<string, IInterceptorSubject>
        {
            ["other"] = new TestContainer(Context) { Name = "Other" }
        };

        // Act - "Child.Name" should fall back to relative path since "Child" is not in inlineSubjects
        var result = Resolver.ResolveValueFromRelativePath("Child.Name", current, inlineSubjects);

        // Assert
        Assert.Equal("ChildName", result);
    }

    [Fact]
    public void ResolveValueFromRelativePath_RelativePath_ResolvesFromCurrentSubject()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "ChildValue" };
        var current = new TestContainer(Context) { Name = "Current", Child = child };

        // Act - "Child.Name" from current
        var result = Resolver.ResolveValueFromRelativePath("Child.Name", current);

        // Assert
        Assert.Equal("ChildValue", result);
    }

    [Fact]
    public void ResolveValueFromRelativePath_EmptyPath_ReturnsNull()
    {
        // Arrange
        var current = new TestContainer(Context) { Name = "Current" };

        // Act
        var result = Resolver.ResolveValueFromRelativePath("", current);

        // Assert
        Assert.Null(result);
    }

    #endregion
}
