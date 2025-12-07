using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Core;
using Moq;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerTests
{
    private static (TypeProvider typeProvider, SubjectTypeRegistry typeRegistry, ConfigurableSubjectSerializer serializer) CreateDependencies()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var mockServiceProvider = new Mock<IServiceProvider>();
        var serializer = new ConfigurableSubjectSerializer(typeRegistry, mockServiceProvider.Object);
        return (typeProvider, typeRegistry, serializer);
    }

    [Fact]
    public void Constructor_InitializesProperties()
    {
        // Arrange
        var (_, typeRegistry, serializer) = CreateDependencies();

        // Act
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Assert
        Assert.Equal("disk", storage.StorageType);
        Assert.Equal(string.Empty, storage.ConnectionString);
        Assert.NotNull(storage.Children);
        Assert.Empty(storage.Children);
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public void Title_ReturnsConnectionStringFileName_WhenNotEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        storage.ConnectionString = @"C:\Users\test\Documents\Storage";

        // Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public void Title_ReturnsStorage_WhenConnectionStringEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act & Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public async Task ConnectAsync_ThrowsWhenConnectionStringEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_ThrowsForUnsupportedStorageType()
    {
        // Arrange
        var (_, typeRegistry, serializer) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        storage.ConnectionString = "test-connection";
        storage.StorageType = "unsupported-type";

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => storage.ConnectAsync());
        Assert.Contains("unsupported-type", ex.Message);
    }

    [Fact]
    public void Status_DefaultsToDisconnected()
    {
        // Arrange
        var (_, typeRegistry, serializer) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Assert - Initial state
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public void Icon_ReturnsStorageIcon()
    {
        // Arrange
        var (_, typeRegistry, serializer) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var icon = storage.Icon;

        // Assert
        Assert.NotNull(icon);
        Assert.True(icon.Contains("<path") || icon.Contains("<g>"), "Icon should be a MudBlazor SVG path string");
    }

    [Fact]
    public void Dispose_SetsStatusToDisconnected()
    {
        // Arrange
        var (_, typeRegistry, serializer) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        storage.Dispose();

        // Assert
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }
}