using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class VirtualFolderTests
{
    private static (TypeProvider typeProvider, SubjectTypeRegistry typeRegistry, ConfigurableSubjectSerializer serializer, IServiceProvider serviceProvider, RootManager rootManager) CreateDependencies()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var context = InterceptorSubjectContext.Create();

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();

        var serviceProvider = services.BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>();
        var rootManager = serviceProvider.GetRequiredService<RootManager>();

        return (typeProvider, typeRegistry, serializer, serviceProvider, rootManager);
    }

    [Fact]
    public void Constructor_InitializesProperties()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Act
        var folder = new VirtualFolder(storage, "test/folder/");

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
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Act
        var folder = new VirtualFolder(storage, "parent/child/");

        // Assert
        Assert.Equal("child", folder.Title);
    }

    [Fact]
    public void Icon_ReturnsFolderIcon()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Act
        var folder = new VirtualFolder(storage, "test/");

        // Assert
        Assert.Equal("Folder", folder.IconName);
    }

    [Fact]
    public void WhenFolderHasNoChildren_ThenChildNamesIsEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);
        var folder = new VirtualFolder(storage, "test/");

        // Act
        var childNames = folder.ChildNames;

        // Assert
        Assert.Empty(childNames);
    }

    [Fact]
    public void WhenFolderHasChildren_ThenChildNamesAreTheKeysOfTheChildren()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);
        var folder = new VirtualFolder(storage, "test/")
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Devices"] = new VirtualFolder(storage, "test/Devices/"),
                ["Notes.md"] = new GenericFile(storage, "test/Notes.md")
            }
        };

        // Act
        var childNames = folder.ChildNames;

        // Assert
        Assert.Equal(["Devices", "Notes.md"], childNames.Order());
    }

    [Fact]
    public void WhenChildrenAreReplacedAfterReading_ThenChildNamesReadBeforeAreUnchanged()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);
        var folder = new VirtualFolder(storage, "test/")
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Motor"] = new VirtualFolder(storage, "test/Motor/")
            }
        };
        var childNames = folder.ChildNames;

        // Act
        folder.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Pump"] = new VirtualFolder(storage, "test/Pump/")
        };

        // Assert
        Assert.Equal(["Motor"], childNames);
        Assert.Equal(["Pump"], folder.ChildNames);
    }
}
