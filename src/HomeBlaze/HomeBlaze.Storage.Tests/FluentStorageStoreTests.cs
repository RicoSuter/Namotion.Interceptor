using FluentStorage;
using FluentStorage.Storage;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// The behavior of the FluentStorage stores that the storage container relies on.
/// </summary>
public sealed class FluentStorageStoreTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-store-");

    public static TheoryData<string> StoreTypes => new() { "disk", "inmemory" };

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task WhenPathIsMissing_ThenReadAndInfoReturnNullAndExistsReturnsFalse(string storeType)
    {
        // Arrange
        using var store = CreateStore(storeType);

        // Act
        var exists = await store.ObjectExists("Docs/Missing.md");
        var info = await store.GetObjectInfo("Docs/Missing.md");
        var stream = await store.OpenRead("Docs/Missing.md");

        // Assert
        Assert.False(exists);
        Assert.Null(info);
        Assert.Null(stream);
    }

    [Fact]
    public async Task WhenExistenceAndInfoOfAPathInAMissingFolderAreAskedOnDisk_ThenTheFolderIsNotCreated()
    {
        // Arrange
        using var store = CreateStore("disk");

        // Act
        await store.ObjectExists("Docs/Notes.md");
        await store.GetObjectInfo("Docs/Notes.md");

        // Assert
        Assert.False(Directory.Exists(Path.Combine(_directory.FullName, "Docs")));
    }

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task WhenStoreIsListedRecursively_ThenFileHasNormalizedPathSizeAndModifiedTime(string storeType)
    {
        // Arrange
        using var store = CreateStore(storeType);
        using (var content = new MemoryStream("abc"u8.ToArray()))
        {
            await store.SetObject("Docs/Notes.md", content, append: false);
        }

        // Act
        var objects = await store.ListObjects(new StorageListOptions { Recurse = true });

        // Assert
        var file = Assert.Single(objects, storeObject => storeObject.IsFile);
        Assert.Equal("Docs/Notes.md", file.FullPath);
        Assert.Equal(3, file.Size);
        Assert.NotNull(file.DateModified);
    }

    [Fact]
    public async Task WhenDiskStoreIsListedRecursively_ThenItsFoldersAreListed()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_directory.FullName, "Docs", "Empty"));
        using var store = CreateStore("disk");

        // Act
        var objects = await store.ListObjects(new StorageListOptions { Recurse = true });

        // Assert
        Assert.Equal(
            new[] { "Docs", "Docs/Empty" },
            objects.Where(storeObject => storeObject.IsFolder).Select(storeObject => storeObject.FullPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WhenAFolderPathIsDeletedOnDisk_ThenTheFolderAndItsContentStay()
    {
        // Arrange
        var folder = Directory.CreateDirectory(Path.Combine(_directory.FullName, "Docs"));
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "Notes.md"), "content");
        using var store = CreateStore("disk");

        // Act
        await store.DeleteObject("Docs");

        // Assert
        Assert.True(File.Exists(Path.Combine(folder.FullName, "Notes.md")));
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private IStore CreateStore(string storeType)
        => storeType == "disk" ? StorageFactory.Disk(_directory.FullName) : StorageFactory.InMemory();
}
