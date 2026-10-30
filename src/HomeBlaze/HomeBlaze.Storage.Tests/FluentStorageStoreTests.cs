using System.Text;
using FluentStorage;
using FluentStorage.Storage;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// The behavior of the FluentStorage stores that the storage container relies on.
/// </summary>
public sealed class FluentStorageStoreTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-store-");

    private static readonly string[] Paths =
    [
        "Notes.md",
        "Docs/Readme.md",
        "Docs/Guides/Set up + 100%.md",
        "Docs/Guides/Ünïcödé.json",
        "Docs/.hidden/file.tmp",
        "a/b/c/d/e.bin"
    ];

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
    public async Task WhenStoreIsListedRecursively_ThenFilesHaveNormalizedPathsSizesAndModifiedTimes(string storeType)
    {
        // Arrange
        using var store = CreateStore(storeType);
        foreach (var path in Paths)
        {
            using var content = new MemoryStream("abc"u8.ToArray());
            await store.SetObject(path, content, append: false);
        }

        // Act
        var objects = await store.ListObjects(new StorageListOptions { Recurse = true });

        // Assert
        var files = objects.Where(storeObject => storeObject.IsFile).ToArray();
        Assert.Equal(
            Paths.Order(StringComparer.Ordinal),
            files.Select(file => file.FullPath).Order(StringComparer.Ordinal));
        Assert.All(files, file =>
        {
            Assert.Equal(3, file.Size);
            Assert.NotNull(file.DateModified);
        });
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

    [Fact]
    public async Task WhenSeparateInMemoryStoresAreWrittenAtOnce_ThenEveryWriteSucceeds()
    {
        // Arrange
        const int storeCount = 8;
        const int writesPerStore = 200;
        var stores = Enumerable.Range(0, storeCount).Select(_ => StorageFactory.InMemory()).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act: each store is written on its own thread of the pool, and all start at once.
        var writes = stores
            .Select(store => Task.Run(async () =>
            {
                await start.Task;
                for (var index = 0; index < writesPerStore; index++)
                {
                    using var content = new MemoryStream(Encoding.UTF8.GetBytes($"# File {index}"));
                    await store.SetObject($"File{index}.md", content, append: false);
                }
            }))
            .ToArray();
        start.SetResult();
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        foreach (var store in stores)
        {
            using (store)
            {
                Assert.Equal(writesPerStore, (await store.ListObjects(new StorageListOptions { Recurse = true })).Count);
                await using var stream = await store.OpenRead($"File{writesPerStore - 1}.md");
                using var reader = new StreamReader(stream);
                Assert.Equal($"# File {writesPerStore - 1}", await reader.ReadToEndAsync());
            }
        }
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private IStore CreateStore(string storeType)
        => storeType == "disk" ? StorageFactory.Disk(_directory.FullName) : StorageFactory.InMemory();
}
