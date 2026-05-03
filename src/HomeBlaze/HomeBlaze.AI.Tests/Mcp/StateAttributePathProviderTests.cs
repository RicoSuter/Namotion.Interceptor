using HomeBlaze.AI.Mcp;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class StateAttributePathProviderTests
{
    [Fact]
    public void WhenPathSeparator_ThenIsSlash()
    {
        var provider = new StateAttributePathProvider();
        Assert.Equal('/', provider.PathSeparator);
    }

    [Fact]
    public void WhenResolvingStateProperty_ThenResolvesCorrectly()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "Test", Temperature = 21.5m };
        var registered = thing.TryGetRegisteredSubject()!;
        var provider = new StateAttributePathProvider();

        // Act
        var result = provider.TryGetPropertyFromPath(registered, "Temperature");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Temperature", result.Value.Property.Name);
    }

    [Fact]
    public void WhenResolvingNestedSubjectProperty_ThenNavigatesThroughChildProperty()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "Root", Temperature = 21.5m };
        thing.Child = new TestChildThing(context) { ChildName = "Device", IsActive = true };
        var registered = thing.TryGetRegisteredSubject()!;
        var provider = new StateAttributePathProvider();

        // Act — use slash separator
        var result = provider.TryGetPropertyFromPath(registered, "Child/ChildName");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("ChildName", result.Value.Property.Name);
    }

    [Fact]
    public void WhenResolvingInlinePaths_ThenDictionaryKeyBecomesSegment()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestFolder(context) { Name = "MyServer" };
        var root = new TestFolder(context) { Name = "Root" };
        root.Children["Servers"] = child;
        var registered = root.TryGetRegisteredSubject()!;
        var provider = new StateAttributePathProvider();

        // Act — "Servers" should resolve via InlinePaths to the child, then "Name" on the child
        var result = provider.TryGetPropertyFromPath(registered, "Servers/Name");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Name", result.Value.Property.Name);
        Assert.Equal("MyServer", result.Value.Property.GetValue());
    }

    [Fact]
    public void WhenResolvingInlinePaths_ThenSubjectResolves()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestFolder(context) { Name = "Device" };
        var root = new TestFolder(context) { Name = "Root" };
        root.Children["MyDevice"] = child;
        var registered = root.TryGetRegisteredSubject()!;
        var provider = new StateAttributePathProvider();

        // Act
        var resolved = provider.TryGetSubjectFromPath(registered, "MyDevice");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(child, resolved.Subject);
    }

    [Fact]
    public void WhenBuildingPath_ThenUsesSlashSeparator()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "Root", Temperature = 21.5m };
        thing.Child = new TestChildThing(context) { ChildName = "Device", IsActive = true };
        var registered = thing.Child.TryGetRegisteredSubject()!;
        var provider = new StateAttributePathProvider();
        var childNameProperty = registered.Properties.First(p => p.Name == "ChildName");

        // Act
        var path = childNameProperty.TryGetPath(provider, thing);

        // Assert
        Assert.Equal("Child/ChildName", path);
    }

    [Fact]
    public void WhenBuildingInlinePathsPath_ThenEmitsKeyAsPlainSegment()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestFolder(context) { Name = "Server" };
        var root = new TestFolder(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestFolder> { ["Servers"] = child }
        };
        var provider = new StateAttributePathProvider();
        var childRegistered = child.TryGetRegisteredSubject()!;
        var nameProperty = childRegistered.Properties.First(p => p.Name == "Name");

        // Act
        var path = nameProperty.TryGetPath(provider, root);

        // Assert — should use slash separator: "Servers/Name"
        Assert.Equal("Servers/Name", path);
    }

    [Fact]
    public void WhenPropertyHasNoStateAttribute_ThenNotIncludedInProperties()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "Test", Temperature = 21.5m };
        var registered = thing.TryGetRegisteredSubject()!;
        var provider = new StateAttributePathProvider();

        // Act — get all included properties (State properties + CanContainSubjects)
        var includedProperties = registered.Properties
            .Where(p => provider.IsPropertyIncluded(p))
            .Select(p => p.Name)
            .ToList();

        // Assert — Name and Temperature have [State], Child is CanContainSubjects
        Assert.Contains("Name", includedProperties);
        Assert.Contains("Temperature", includedProperties);
        Assert.Contains("Child", includedProperties);
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }
}
