using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class StorageHierarchyManagerTests
{
    private static (FluentStorageContainer storage, IInterceptorSubjectContext context) CreateStorage()
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

        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);
        return (storage, context);
    }

    /// <summary>
    /// Creates a mock IInterceptorSubject that also implements IConfigurable.
    /// </summary>
    private static IInterceptorSubject CreateConfigurableSubject()
    {
        var mock = new Mock<IInterceptorSubject>();
        mock.As<IConfigurable>();
        return mock.Object;
    }

    /// <summary>
    /// Creates a mock IInterceptorSubject that does NOT implement IConfigurable.
    /// </summary>
    private static IInterceptorSubject CreateNonConfigurableSubject()
    {
        var mock = new Mock<IInterceptorSubject>();
        return mock.Object;
    }

    [Fact]
    public void PlaceInHierarchy_ConfigurableSubject_UsesKeyWithoutExtension()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var subject = CreateConfigurableSubject();

        // Act
        manager.PlaceInHierarchy("motor.json", subject, children, storage);

        // Assert
        Assert.True(children.ContainsKey("motor"), "Key should be 'motor' without extension");
        Assert.False(children.ContainsKey("motor.json"), "Key should NOT include .json extension");
        Assert.Same(subject, children["motor"]);
    }

    [Fact]
    public void PlaceInHierarchy_NonConfigurableSubject_UsesKeyWithExtension()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var subject = CreateNonConfigurableSubject();

        // Act
        manager.PlaceInHierarchy("data.json", subject, children, storage);

        // Assert
        Assert.True(children.ContainsKey("data.json"), "Key should be 'data.json' with extension");
        Assert.False(children.ContainsKey("data"), "Key should NOT be without extension");
        Assert.Same(subject, children["data.json"]);
    }

    [Fact]
    public void PlaceInHierarchy_MarkdownFile_UsesKeyWithExtension()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var subject = CreateNonConfigurableSubject();

        // Act
        manager.PlaceInHierarchy("readme.md", subject, children, storage);

        // Assert
        Assert.True(children.ContainsKey("readme.md"), "Key should be 'readme.md' with extension");
        Assert.False(children.ContainsKey("readme"), "Key should NOT be without extension");
    }

    [Fact]
    public void PlaceInHierarchy_Collision_SkipsSecondSubjectAndLogsWarning()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var firstSubject = CreateConfigurableSubject();
        var secondSubject = CreateNonConfigurableSubject();

        // Act - first subject claims key "motor"
        manager.PlaceInHierarchy("motor.json", firstSubject, children, storage);
        // Act - second subject tries to claim same key "motor" (file without extension)
        manager.PlaceInHierarchy("motor", secondSubject, children, storage);

        // Assert - first subject wins, second is skipped
        Assert.Single(children);
        Assert.Same(firstSubject, children["motor"]);
    }

    [Fact]
    public void PlaceInHierarchy_NestedConfigurableSubject_UsesKeyWithoutExtension()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var subject = CreateConfigurableSubject();

        // Act
        manager.PlaceInHierarchy("Demo/motor.json", subject, children, storage);

        // Assert
        Assert.True(children.ContainsKey("Demo"), "Folder should exist");
        var folder = children["Demo"] as VirtualFolder;
        Assert.NotNull(folder);
        Assert.True(folder.Children.ContainsKey("motor"), "Key should be 'motor' without extension");
        Assert.False(folder.Children.ContainsKey("motor.json"), "Key should NOT include .json extension");
    }

    [Fact]
    public void RemoveFromHierarchy_ConfigurableSubject_RemovesKeyWithoutExtension()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var subject = CreateConfigurableSubject();
        manager.PlaceInHierarchy("motor.json", subject, children, storage);

        // Act
        manager.RemoveFromHierarchy("motor.json", subject, children);

        // Assert
        Assert.Empty(children);
    }

    [Fact]
    public void RemoveFromHierarchy_NestedConfigurableSubject_RemovesKeyWithoutExtension()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var subject = CreateConfigurableSubject();
        manager.PlaceInHierarchy("Demo/motor.json", subject, children, storage);

        // Act
        manager.RemoveFromHierarchy("Demo/motor.json", subject, children);

        // Assert
        var folder = children["Demo"] as VirtualFolder;
        Assert.NotNull(folder);
        Assert.Empty(folder.Children);
    }

    [Fact]
    public void WhenFolderIsEmpty_ThenPlaceWithNullSubjectCreatesVirtualFolder()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();

        // Act
        manager.PlaceInHierarchy("EmptyFolder/", null, children, storage);

        // Assert
        Assert.True(children.ContainsKey("EmptyFolder"));
        var folder = children["EmptyFolder"] as VirtualFolder;
        Assert.NotNull(folder);
        Assert.Empty(folder.Children);
    }

    [Fact]
    public void WhenNestedFolderIsEmpty_ThenPlaceWithNullSubjectCreatesAllFolders()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();

        // Act
        manager.PlaceInHierarchy("Parent/Child/Grandchild/", null, children, storage);

        // Assert
        var parent = children["Parent"] as VirtualFolder;
        Assert.NotNull(parent);
        var child = parent.Children["Child"] as VirtualFolder;
        Assert.NotNull(child);
        var grandchild = child.Children["Grandchild"] as VirtualFolder;
        Assert.NotNull(grandchild);
        Assert.Empty(grandchild.Children);
    }

    [Fact]
    public void WhenFolderAlreadyExists_ThenPlaceWithNullSubjectPreservesChildren()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var subject = CreateConfigurableSubject();
        manager.PlaceInHierarchy("Demo/motor.json", subject, children, storage);

        // Act
        manager.PlaceInHierarchy("Demo/", null, children, storage);

        // Assert
        var folder = children["Demo"] as VirtualFolder;
        Assert.NotNull(folder);
        Assert.True(folder.Children.ContainsKey("motor"), "Existing child should be preserved");
    }

    [Fact]
    public void WhenKeyHoldsAnotherInstance_ThenRemoveKeepsTheEntry()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        manager.PlaceInHierarchy("Motor1/", null, children, storage);
        var folder = children["Motor1"];
        var placeholder = new UnknownSubject(storage, "Motor1.json", "HomeBlaze.Samples.Motor", UnknownSubject.TypeNotLoadedReason);

        // Act
        manager.RemoveFromHierarchy("Motor1.json", placeholder, children);

        // Assert
        Assert.Same(folder, children["Motor1"]);
    }

    [Fact]
    public void WhenNestedKeyHoldsAnotherInstance_ThenRemoveKeepsTheEntry()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var subject = CreateConfigurableSubject();
        manager.PlaceInHierarchy("Demo/motor.json", subject, children, storage);
        var otherSubject = CreateConfigurableSubject();

        // Act
        manager.RemoveFromHierarchy("Demo/motor.json", otherSubject, children);

        // Assert
        var folder = Assert.IsType<VirtualFolder>(children["Demo"]);
        Assert.Same(subject, folder.Children["motor"]);
    }

    [Fact]
    public void WhenReplacingNestedLeaf_ThenNewSubjectTakesTheKey()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        var placeholder = new UnknownSubject(storage, "Demo/motor.json", "HomeBlaze.Samples.Motor", UnknownSubject.TypeNotLoadedReason);
        manager.PlaceInHierarchy("Demo/motor.json", placeholder, children, storage);
        var replacement = CreateConfigurableSubject();

        // Act
        var replaced = manager.ReplaceInHierarchy("Demo/motor.json", placeholder, replacement, children);

        // Assert
        Assert.True(replaced);
        var folder = Assert.IsType<VirtualFolder>(children["Demo"]);
        Assert.Same(replacement, folder.Children["motor"]);
    }

    [Fact]
    public void WhenReplacingLeafWhoseKeyHoldsAnotherInstance_ThenNothingChanges()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var children = new Dictionary<string, IInterceptorSubject>();
        manager.PlaceInHierarchy("Motor1/", null, children, storage);
        var folder = children["Motor1"];
        var placeholder = new UnknownSubject(storage, "Motor1.json", "HomeBlaze.Samples.Motor", UnknownSubject.TypeNotLoadedReason);

        // Act
        var replaced = manager.ReplaceInHierarchy("Motor1.json", placeholder, CreateConfigurableSubject(), children);

        // Assert
        Assert.False(replaced);
        Assert.Same(folder, children["Motor1"]);
    }

    [Fact]
    public void WhenBuildingTheSameEntriesAgain_ThenPreviousChildrenAndFoldersAreKept()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var file = CreateNonConfigurableSubject();
        var nestedFile = CreateNonConfigurableSubject();
        (string, IInterceptorSubject?)[] entries = [("file.txt", file), ("folder/", null), ("folder/nested.txt", nestedFile)];
        var children = manager.BuildHierarchy(entries, new Dictionary<string, IInterceptorSubject>(), storage);
        var folder = Assert.IsType<VirtualFolder>(children["folder"]);
        var folderChildren = folder.Children;

        // Act
        var rebuiltChildren = manager.BuildHierarchy(entries, children, storage);

        // Assert
        Assert.Same(children, rebuiltChildren);
        Assert.Same(folderChildren, folder.Children);
        Assert.Same(nestedFile, folder.Children["nested.txt"]);
    }

    [Fact]
    public void WhenBuildingChangedEntries_ThenFoldersAreReusedAndMissingFoldersDropped()
    {
        // Arrange
        var (storage, _) = CreateStorage();
        var manager = new StorageHierarchyManager();
        var nestedFile = CreateNonConfigurableSubject();
        var addedFile = CreateNonConfigurableSubject();
        var children = manager.BuildHierarchy(
            [("kept/nested.txt", nestedFile), ("gone/other.txt", CreateNonConfigurableSubject())],
            new Dictionary<string, IInterceptorSubject>(),
            storage);
        var keptFolder = Assert.IsType<VirtualFolder>(children["kept"]);

        // Act
        var rebuiltChildren = manager.BuildHierarchy(
            [("kept/nested.txt", nestedFile), ("kept/added.txt", addedFile)], children, storage);

        // Assert
        Assert.NotSame(children, rebuiltChildren);
        Assert.Equal(["kept"], rebuiltChildren.Keys);
        Assert.Same(keptFolder, rebuiltChildren["kept"]);
        Assert.Same(nestedFile, keptFolder.Children["nested.txt"]);
        Assert.Same(addedFile, keptFolder.Children["added.txt"]);
    }
}
