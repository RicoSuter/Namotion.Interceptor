using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Host.Services.Components;
using HomeBlaze.Services;

namespace HomeBlaze.Host.Services.Tests;

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
    public void HasComponent_ExistingComponent_ReturnsTrue()
    {
        // Arrange
        var registry = CreateRegistryWithScanning();

        // Act & Assert
        Assert.True(registry.HasComponent(typeof(TestSubject), SubjectComponentType.Edit));
        Assert.False(registry.HasComponent(typeof(TestSubject), SubjectComponentType.Page));
    }

    // Test fixtures
    public class TestSubject { }

    [SubjectComponent(SubjectComponentType.Edit, typeof(TestSubject))]
    public class TestEditComponent { }
}
