using System.Text;

namespace HomeBlaze.Services.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AtomicFileTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void WhenBytesAreWrittenToAMissingDirectory_ThenTheFileHoldsThemAndNoTemporaryFileRemains()
    {
        // Arrange
        var path = Path.Combine(_directory, "Nested", "file.json");

        // Act
        AtomicFile.WriteAllBytes(path, "{}"u8);

        // Assert
        Assert.Equal("{}"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task WhenTextReplacesAFile_ThenTheFileHoldsItAsUtf8WithoutAByteOrderMark()
    {
        // Arrange
        var path = Path.Combine(_directory, "file.txt");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, "old content");

        // Act
        await AtomicFile.WriteAllTextAsync(path, "Grüße", CancellationToken.None);

        // Assert
        Assert.Equal(Encoding.UTF8.GetBytes("Grüße"), await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task WhenTheMoveFails_ThenTheTemporaryFileIsDeleted()
    {
        // Arrange
        var path = Path.Combine(_directory, "taken");
        Directory.CreateDirectory(path);

        // Act
        var syncException = Record.Exception(() => AtomicFile.WriteAllBytes(path, [1]));
        var asyncException = await Record.ExceptionAsync(() => AtomicFile.WriteAllBytesAsync(path, new byte[] { 1 }, CancellationToken.None));

        // Assert
        Assert.NotNull(syncException);
        Assert.NotNull(asyncException);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("file.json.0123456789abcdef0123456789abcdef.tmp", true)]
    [InlineData("file.json.0123456789ABCDEF0123456789ABCDEF.tmp", false)]
    [InlineData("file.json.notes.tmp", false)]
    [InlineData("file.json.x.json.0123456789abcdef0123456789abcdef.tmp", false)]
    [InlineData("file.jsonx0123456789abcdef0123456789abcdef.tmp", false)]
    [InlineData("file.json.0123456789abcdef0123456789abcdef.txt", false)]
    public void WhenAFileNameIsChecked_ThenOnlyTheNameOfATemporaryFileOfTheTargetIsOne(string fileName, bool isTemporaryFile)
    {
        // Act
        var result = AtomicFile.IsTemporaryFile(fileName, "file.json");

        // Assert
        Assert.Equal(isTemporaryFile, result);
    }
}
