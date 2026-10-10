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

    [Fact]
    public async Task WhenDirectoryIsListed_ThenPathFromBlobEqualsItsNormalizedFullPath()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-path-");
        try
        {
            foreach (var path in Paths)
            {
                var fullPath = Path.Combine(directory.FullName, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, "content");
            }

            using var storage = StorageFactory.Blobs.DirectoryFiles(directory.FullName);

            // Act
            var blobs = await storage.ListAsync(recurse: true);

            // Assert
            Assert.Equal(
                Paths.Order(StringComparer.Ordinal),
                blobs.Where(blob => blob.IsFile).Select(StoragePath.FromBlob).Order(StringComparer.Ordinal));
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
    public void WhenBlobIsCreatedFromFolderAndName_ThenPathFromBlobEqualsItsNormalizedFullPath(
        string folderPath, string name, string expectedPath)
    {
        // Arrange
        var blob = new Blob(folderPath, name, BlobItemKind.File);

        // Act
        var path = StoragePath.FromBlob(blob);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Equal(StoragePath.Normalize(blob.FullPath), path);
    }

    [Theory]
    [InlineData("/", "")]
    [InlineData("Notes.md", "Notes.md")]
    [InlineData("/Docs/Guides/Setup.md", "Docs/Guides/Setup.md")]
    [InlineData("Docs//Guides/", "Docs/Guides")]
    public void WhenBlobIsCreatedFromFullPath_ThenPathFromBlobEqualsItsNormalizedFullPath(
        string fullPath, string expectedPath)
    {
        // Arrange: an in-memory storage creates the blobs of its listing this way.
        var blob = new Blob(fullPath);

        // Act
        var path = StoragePath.FromBlob(blob);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Equal(StoragePath.Normalize(blob.FullPath), path);
    }
}
