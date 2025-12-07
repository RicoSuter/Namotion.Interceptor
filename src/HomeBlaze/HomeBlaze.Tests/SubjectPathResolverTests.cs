using HomeBlaze.Core.Pages;
using HomeBlaze.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Tests;

public class SubjectPathResolverTests
{
    private readonly IInterceptorSubjectContext _context;
    private readonly ISubjectRegistry _registry;

    public SubjectPathResolverTests()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithRegistry();
        _registry = _context.GetService<ISubjectRegistry>()!;
    }

    [Fact]
    public void ResolveSubject_EmptyPath_ReturnsRoot()
    {
        // Arrange
        var root = new TestContainer(_context) { Name = "Root" };

        // Act
        var result = SubjectPathResolver.ResolveSubject(root, _registry, "");

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_NullPath_ReturnsRoot()
    {
        // Arrange
        var root = new TestContainer(_context) { Name = "Root" };

        // Act
        var result = SubjectPathResolver.ResolveSubject(root, _registry, null!);

        // Assert
        Assert.Same(root, result);
    }

    [Fact]
    public void ResolveSubject_SinglePropertyPath_ReturnsChild()
    {
        // Arrange
        var child = new TestContainer { Name = "Child" };
        var root = new TestContainer(_context)
        {
            Name = "Root",
            Child = child
        };

        // Act
        var result = SubjectPathResolver.ResolveSubject(root, _registry, "Child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void ResolveSubject_DictionaryPath_ReturnsChildByKey()
    {
        // Arrange
        var notes = new TestContainer(_context) { Name = "Notes" };
        var root = new TestContainer(_context) { Name = "Root" };
        root.Children["Notes"] = notes;

        // Act
        var result = SubjectPathResolver.ResolveSubject(root, _registry, "Children/Notes");

        // Assert
        Assert.Same(notes, result);
    }

    [Fact]
    public void ResolveSubject_InvalidPath_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(_context) { Name = "Root" };

        // Act
        var result = SubjectPathResolver.ResolveSubject(root, _registry, "NonExistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSubject_InvalidDictionaryKey_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(_context) { Name = "Root" };
        root.Children["Notes"] = new TestContainer(_context) { Name = "Notes" };

        // Act
        var result = SubjectPathResolver.ResolveSubject(root, _registry, "Children/NonExistent");

        // Assert
        Assert.Null(result);
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
        var result = SubjectPathResolver.ResolveSubject(root, _registry, "Children/Notes/Child");

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
        var result = SubjectPathResolver.ResolveSubject(root, _registry, "Children/My%20File.md");

        // Assert
        Assert.Same(file, result);
    }

    [Fact]
    public void ResolveSubject_IncompleteDictionaryPath_ReturnsNull()
    {
        // Arrange
        var root = new TestContainer(_context) { Name = "Root" };
        root.Children["Notes"] = new TestContainer(_context) { Name = "Notes" };

        // Act - path ends at collection property without index
        var result = SubjectPathResolver.ResolveSubject(root, _registry, "Children");

        // Assert
        Assert.Null(result);
    }
}
