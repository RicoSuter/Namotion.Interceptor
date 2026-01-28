using HomeBlaze.Services;

namespace HomeBlaze.Services.Tests;

public class TypeProviderTests
{
    [Fact]
    public void Types_ReturnsEmptyInitially()
    {
        // Arrange
        var provider = new TypeProvider();

        // Act
        var types = provider.Types;

        // Assert
        Assert.Empty(types);
    }

    [Fact]
    public void AddAssembly_AddsTypesFromAssembly()
    {
        // Arrange
        var provider = new TypeProvider();

        // Act
        provider.AddAssembly(typeof(TypeProvider).Assembly);
        var types = provider.Types;

        // Assert
        Assert.NotEmpty(types);
        Assert.Contains(types, t => t == typeof(TypeProvider));
    }
}
