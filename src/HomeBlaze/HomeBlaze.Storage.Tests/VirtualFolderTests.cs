using HomeBlaze.Core;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class VirtualFolderTests
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
        var context = InterceptorSubjectContext.Create();
        var (_, typeRegistry, serializer) = CreateDependencies();
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
        var (_, typeRegistry, serializer) = CreateDependencies();
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
        var (_, typeRegistry, serializer) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer);

        // Act
        var folder = new VirtualFolder(context, storage, "test/");

        // Assert
        Assert.NotNull(folder.Icon);
        Assert.True(folder.Icon.Contains("<path") || folder.Icon.Contains("<g>"), "Icon should be a MudBlazor SVG path string");
    }
}