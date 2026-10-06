using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

public class SubjectTypeRegistryTests
{
    [Fact]
    public void WhenTypeIsAddedAfterFirstLookup_ThenItResolves()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        var registry = new SubjectTypeRegistry(typeProvider);
        Assert.Null(registry.ResolveType(typeof(TestContainer).FullName!));

        // Act
        typeProvider.AddTypes([typeof(TestContainer)]);

        // Assert
        Assert.Equal(typeof(TestContainer), registry.ResolveType(typeof(TestContainer).FullName!));
        Assert.Contains(typeof(TestContainer), registry.RegisteredTypes);
    }
}
