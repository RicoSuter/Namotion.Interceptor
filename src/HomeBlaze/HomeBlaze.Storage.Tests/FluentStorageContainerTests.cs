using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Services;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerTests
{
    private static (TypeProvider typeProvider, SubjectTypeRegistry typeRegistry, ConfigurableSubjectSerializer serializer, SubjectPathResolver pathResolver, RootManager rootManager) CreateDependencies()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var mockServiceProvider = new Mock<IServiceProvider>();
        var serializer = new ConfigurableSubjectSerializer(typeRegistry, mockServiceProvider.Object);
        var context = InterceptorSubjectContext.Create();
        var rootManager = new RootManager(typeRegistry, serializer, context);
        var pathResolver = new SubjectPathResolver(rootManager, context);
        return (typeProvider, typeRegistry, serializer, pathResolver, rootManager);
    }

    [Fact]
    public void Constructor_InitializesProperties()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, _) = CreateDependencies();

        // Act
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver);

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
        var (_, typeRegistry, serializer, pathResolver, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver);

        // Act
        storage.ConnectionString = @"Foo/Bar/Storage";

        // Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public void Title_ReturnsStorage_WhenConnectionStringEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver);

        // Act & Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public async Task ConnectAsync_ThrowsWhenConnectionStringEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_ThrowsForUnsupportedStorageType()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver);

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
        var (_, typeRegistry, serializer, pathResolver, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver);

        // Assert - Initial state
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public void Icon_ReturnsStorageIcon()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver);

        // Act
        var icon = storage.Icon;

        // Assert
        Assert.Equal("Storage", icon);
    }

    [Fact]
    public void Dispose_SetsStatusToDisconnected()
    {
        // Arrange
        var (_, typeRegistry, serializer, pathResolver, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver);

        // Act
        storage.Dispose();

        // Assert
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }
}