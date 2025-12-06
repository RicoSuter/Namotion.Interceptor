using HomeBlaze.Core.Services;
using HomeBlaze.Storage;
using Namotion.Interceptor;

namespace HomeBlaze.Tests;

public class FluentStorageContainerTests
{
    [Fact]
    public void Constructor_InitializesProperties()
    {
        // Arrange
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);

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
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
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
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act & Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public async Task ConnectAsync_ThrowsWhenConnectionStringEmpty()
    {
        // Arrange
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_ThrowsForUnsupportedStorageType()
    {
        // Arrange
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
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
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Assert - Initial state
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public void Icon_ReturnsStorageIcon()
    {
        // Arrange
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var icon = storage.Icon;

        // Assert
        Assert.NotNull(icon);
        Assert.Contains("svg", icon);
    }

    [Fact]
    public void Dispose_SetsStatusToDisconnected()
    {
        // Arrange
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        storage.Dispose();

        // Assert
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }
}

public class VirtualFolderTests
{
    [Fact]
    public void Constructor_InitializesProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var folder = new VirtualFolder(context, storage, "test/folder/");

        // Assert
        Assert.Same(storage, folder.Storage);
        Assert.Equal("test/folder/", folder.RelativePath);
        Assert.NotNull(folder.Children);
        Assert.Empty(folder.Children);
    }

    [Fact]
    public void Title_ReturnsFolderName()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var folder = new VirtualFolder(context, storage, "parent/child/");

        // Assert
        Assert.Equal("child", folder.Title);
    }

    [Fact]
    public void Icon_ReturnsFolderIcon()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var folder = new VirtualFolder(context, storage, "test/");

        // Assert
        Assert.NotNull(folder.Icon);
        Assert.Contains("svg", folder.Icon);
    }
}

public class JsonFileTests
{
    [Fact]
    public void Constructor_FluentStorage_InitializesProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var file = new HomeBlaze.Storage.Files.JsonFile(context, storage, "data/config.json");

        // Assert
        Assert.Same(storage, file.Storage);
        Assert.Equal("data/config.json", file.BlobPath);
        Assert.Equal("config.json", file.FileName);
        Assert.Equal(string.Empty, file.Content);
    }

    [Fact]
    public void Title_ReturnsFileNameWithoutExtension()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var file = new HomeBlaze.Storage.Files.JsonFile(context, storage, "data/my-config.json");

        // Assert
        Assert.Equal("my-config", file.Title);
    }

    [Fact]
    public void Icon_ReturnsJsonIcon()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var typeRegistry = new SubjectTypeRegistry();
        var serializer = new SubjectSerializer(typeRegistry, null);
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var file = new HomeBlaze.Storage.Files.JsonFile(context, storage, "test.json");

        // Assert
        Assert.NotNull(file.Icon);
        Assert.Contains("svg", file.Icon);
    }

    [Fact]
    public void DefaultConstructor_InitializesEmptyProperties()
    {
        // Act
        var file = new HomeBlaze.Storage.Files.JsonFile();

        // Assert
        Assert.Null(file.Storage);
        Assert.Null(file.BlobPath);
        Assert.Equal(string.Empty, file.FilePath);
        Assert.Equal(string.Empty, file.FileName);
        Assert.Equal(string.Empty, file.Content);
    }
}
