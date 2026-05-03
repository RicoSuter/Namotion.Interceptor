using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests for SubjectPathResolver.ResolveValue() extension method.
/// </summary>
public class SubjectPathResolverResolveValueTests : SubjectPathResolverTestBase
{
    [Fact]
    public void ResolveValue_AbsoluteChildProperty_ReturnsValue()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "TestChild" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - "/Child/Name" resolves the Name property on child
        var result = Resolver.ResolveValue("/Child/Name", PathStyle.Canonical);

        // Assert
        Assert.Equal("TestChild", result);
    }

    [Fact]
    public void ResolveValue_PropertyOnRoot_ReturnsValue()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "RootValue" };
        RootManager.Root = root;

        // Act - "/Name" resolves the Name property on root
        var result = Resolver.ResolveValue("/Name", PathStyle.Canonical);

        // Assert
        Assert.Equal("RootValue", result);
    }

    [Fact]
    public void ResolveValue_RelativeChildProperty_ReturnsValue()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "GrandchildValue" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - "Child/Name" relative to child resolves grandchild's Name
        var result = Resolver.ResolveValue("Child/Name", PathStyle.Canonical, child);

        // Assert
        Assert.Equal("GrandchildValue", result);
    }

    [Fact]
    public void ResolveValue_PropertyNameOnly_ResolvesOnRelativeTo()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "ChildName" };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - "Name" (no slash) is just a property name on relativeTo
        var result = Resolver.ResolveValue("Name", PathStyle.Canonical, child);

        // Assert
        Assert.Equal("ChildName", result);
    }

    [Fact]
    public void ResolveValue_ExplicitRelative_ReturnsValue()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "GrandchildValue" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;

        // Act - "./Child/Name" from child resolves grandchild's Name
        var result = Resolver.ResolveValue("./Child/Name", PathStyle.Canonical, child);

        // Assert
        Assert.Equal("GrandchildValue", result);
    }

    [Fact]
    public void ResolveValue_ParentProperty_ReturnsValue()
    {
        // Arrange
        var child = new TestContainer(Context) { Name = "ChildName" };
        var root = new TestContainer(Context) { Name = "RootName", Child = child };
        RootManager.Root = root;

        // Act - "../Name" from child resolves root's Name
        var result = Resolver.ResolveValue("../Name", PathStyle.Canonical, child);

        // Assert
        Assert.Equal("RootName", result);
    }

    [Fact]
    public void ResolveValue_InlinePathsRelativeLookup_ReturnsValue()
    {
        // Arrange
        var motor = new TestContainerWithChildren(Context) { Name = "Motor1" };
        var root = new TestContainerWithChildren(Context) { Name = "Root" };
        root.Children["motor"] = motor;
        RootManager.Root = root;

        // Act - "motor/Name" relative to root resolves via InlinePaths
        var result = Resolver.ResolveValue("motor/Name", PathStyle.Canonical, root);

        // Assert
        Assert.Equal("Motor1", result);
    }

    [Fact]
    public void ResolveValue_EmptyPath_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act
        var result = Resolver.ResolveValue("", PathStyle.Canonical);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveValue_PathEndingWithSlash_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act - trailing slash means empty property name
        var result = Resolver.ResolveValue("/Child/", PathStyle.Canonical);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveValue_InvalidSubjectPath_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;

        // Act - "/NonExistent/Name" fails to resolve the subject
        var result = Resolver.ResolveValue("/NonExistent/Name", PathStyle.Canonical);

        // Assert
        Assert.Null(result);
    }
}
