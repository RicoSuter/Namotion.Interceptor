using FluentStorage;
using FluentStorage.Blobs;
using StoragePath = HomeBlaze.Storage.Internal.StoragePath;

namespace HomeBlaze.Storage.Tests;

public class StoragePathTests
{
    private static readonly string[] Paths =
    [
        "Notes.md",
        "Docs/Readme.md",
        "Docs/Guides/Set up + 100%.md",
        "Docs/Guides/Ünïcödé.json",
        "Docs/.hidden/file.tmp",
        "a/b/c/d/e.bin"
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenBlobsAreListed_ThenPathFromBlobEqualsItsNormalizedFullPath(bool onDisk)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-path-");
        try
        {
            using var storage = onDisk
                ? StorageFactory.Blobs.DirectoryFiles(directory.FullName)
                : StorageFactory.Blobs.InMemory();

            foreach (var path in Paths)
            {
                using var content = new MemoryStream([1]);
                await storage.WriteAsync(path, content);
            }

            // Act
            var blobs = await storage.ListAsync(recurse: true);

            // Assert
            Assert.Equal(Paths.Order(StringComparer.Ordinal), blobs.Where(blob => blob.IsFile).Select(StoragePath.FromBlob).Order(StringComparer.Ordinal));
            Assert.All(blobs, blob => Assert.Equal(StoragePath.Normalize(blob.FullPath), StoragePath.FromBlob(blob)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("/", "", "")]
    [InlineData("/", "Notes.md", "Notes.md")]
    [InlineData("/Docs", "Readme.md", "Docs/Readme.md")]
    [InlineData("Docs/Guides/", "/Setup.md", "Docs/Guides/Setup.md")]
    public void WhenBlobHasFolderAndName_ThenPathFromBlobEqualsItsNormalizedFullPath(string folderPath, string name, string expectedPath)
    {
        // Arrange
        var blob = new Blob(folderPath, name, BlobItemKind.File);

        // Act
        var path = StoragePath.FromBlob(blob);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Equal(StoragePath.Normalize(blob.FullPath), path);
    }
}
