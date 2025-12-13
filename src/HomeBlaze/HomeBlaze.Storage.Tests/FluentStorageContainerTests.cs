using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Navigation;
using Moq;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerTests
{
    private static (TypeProvider typeProvider, SubjectTypeRegistry typeRegistry, ConfigurableSubjectSerializer serializer, SubjectPathResolver pathResolver, RootManager rootManager) CreateDependencies()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var mockServiceProvider = new Mock<IServiceProvider>();
        var serializer = new ConfigurableSubjectSerializer(typeRegistry, mockServiceProvider.Object);
        var pathResolver = new SubjectPathResolver();
        var rootManager = new RootManager(typeRegistry, serializer, null!);
        return (typeProvider, typeRegistry, serializer, pathResolver, rootManager);
    }

    [Fact]
    public void Constructor_InitializesProperties()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();

        // Act
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

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
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

        // Act
        storage.ConnectionString = @"Foo/Bar/Storage";

        // Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public void Title_ReturnsStorage_WhenConnectionStringEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

        // Act & Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public async Task ConnectAsync_ThrowsWhenConnectionStringEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_ThrowsForUnsupportedStorageType()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

        storage.ConnectionString = "test-connection";
        storage.StorageType = "unsupported-type";

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => storage.ConnectAsync(CancellationToken.None));
        Assert.Contains("unsupported-type", ex.Message);
    }

    [Fact]
    public void Status_DefaultsToDisconnected()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

        // Assert - Initial state
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public void Icon_ReturnsStorageIcon()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

        // Act
        var icon = storage.Icon;

        // Assert
        Assert.Equal("Storage", icon);
    }

    [Fact]
    public void Dispose_SetsStatusToDisconnected()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

        // Act
        storage.Dispose();

        // Assert
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }
}