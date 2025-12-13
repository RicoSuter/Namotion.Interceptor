using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Components;
using HomeBlaze.Services;

namespace HomeBlaze.Services.Tests;

public class SubjectComponentRegistryTests
{
    private static SubjectComponentRegistry CreateRegistryWithScanning()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssemblies(typeof(SubjectComponentRegistryTests).Assembly);
        return new SubjectComponentRegistry(typeProvider);
    }

    [Fact]
    public void ScanAssemblies_FindsAttributedComponents()
    {
        // Arrange
        var registry = CreateRegistryWithScanning();

        // Act - Lazy scanning happens on first access
        var component = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Edit);

        // Assert
        Assert.NotNull(component);
        Assert.Equal(typeof(TestEditComponent), component.ComponentType);
    }

    [Fact]
    public void GetComponent_ExactMatch_ReturnsRegistration()
    {
        // Arrange
        var registry = CreateRegistryWithScanning();

        // Act
        var result = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Edit);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(typeof(TestEditComponent), result.ComponentType);
        Assert.Equal(typeof(TestSubject), result.SubjectType);
        Assert.Equal(SubjectComponentType.Edit, result.Type);
        Assert.Null(result.Name);
    }

    [Fact]
    public void GetComponent_NoMatch_ReturnsNull()
    {
        // Arrange - empty registry (no types added)
        var typeProvider = new TypeProvider();
        var registry = new SubjectComponentRegistry(typeProvider);

        // Act
        var result = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Page);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetComponent_WithName_ReturnsNamedRegistration()
    {
        // Arrange
        var registry = CreateRegistryWithScanning();

        // Act
        var result1 = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Widget, "status");
        var result2 = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Widget, "temperature");

        // Assert
        Assert.NotNull(result1);
        Assert.Equal(typeof(TestWidgetComponent), result1.ComponentType);
        Assert.Equal("status", result1.Name);

        Assert.NotNull(result2);
        Assert.Equal(typeof(TestWidget2Component), result2.ComponentType);
        Assert.Equal("temperature", result2.Name);
    }

    [Fact]
    public void GetComponents_ReturnsAllOfType()
    {
        // Arrange
        var registry = CreateRegistryWithScanning();

        // Act
        var widgets = registry.GetComponents(typeof(TestSubject), SubjectComponentType.Widget).ToList();

        // Assert
        Assert.Equal(2, widgets.Count);
        Assert.Contains(widgets, w => w.Name == "status");
        Assert.Contains(widgets, w => w.Name == "temperature");
    }

    [Fact]
    public void HasComponent_ExistingComponent_ReturnsTrue()
    {
        // Arrange
        var registry = CreateRegistryWithScanning();

        // Act & Assert
        Assert.True(registry.HasComponent(typeof(TestSubject), SubjectComponentType.Edit));
        Assert.False(registry.HasComponent(typeof(TestSubject), SubjectComponentType.Page));
    }

    [Fact]
    public void ScanAssemblies_FindsMultipleAttributes()
    {
        // Arrange
        var registry = CreateRegistryWithScanning();

        // Act
        var edit = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Edit);
        var widget1 = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Widget, "status");
        var widget2 = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Widget, "temperature");

        // Assert
        Assert.NotNull(edit);
        Assert.NotNull(widget1);
        Assert.NotNull(widget2);
    }

    // Test fixtures
    public class TestSubject { }

    [SubjectComponent(SubjectComponentType.Edit, typeof(TestSubject))]
    public class TestEditComponent { }

    [SubjectComponent(SubjectComponentType.Widget, typeof(TestSubject), Name = "status")]
    public class TestWidgetComponent { }

    [SubjectComponent(SubjectComponentType.Widget, typeof(TestSubject), Name = "temperature")]
    public class TestWidget2Component { }
}
