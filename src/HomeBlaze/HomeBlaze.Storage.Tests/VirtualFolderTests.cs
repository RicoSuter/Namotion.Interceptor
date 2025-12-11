using HomeBlaze.Services;
using HomeBlaze.Services.Navigation;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class VirtualFolderTests
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
        var context = InterceptorSubjectContext.Create();
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

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
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

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
        var (_, typeRegistry, serializer, pathResolver, rootManager) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, pathResolver, rootManager);

        // Act
        var folder = new VirtualFolder(context, storage, "test/");

        // Assert
        Assert.Equal("Folder", folder.Icon);
    }
}
