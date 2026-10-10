using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Moq;

namespace HomeBlaze.Storage.Tests;

public class GenericFileTests
{
    private static readonly DateTime Modified = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task WhenGenericFileChanges_ThenSizeAndTimeComeFromTheStorage()
    {
        // Arrange
        var storage = new Mock<IStorageContainer>();
        storage
            .Setup(s => s.GetBlobMetadataAsync("/Data/Report.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobMetadata(1234, Modified));
        var file = new GenericFile(storage.Object, "/Data/Report.pdf");

        // Act
        await file.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1234, file.FileSize);
        Assert.Equal(Modified, file.LastModified);
    }

    [Fact]
    public async Task WhenJsonFileChanges_ThenSizeAndTimeComeFromTheStorage()
    {
        // Arrange
        var storage = new Mock<IStorageContainer>();
        storage
            .Setup(s => s.GetBlobMetadataAsync("/Data/Values.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobMetadata(56, Modified));
        var file = new JsonFile(storage.Object, "/Data/Values.json");

        // Act
        await file.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal(56, file.FileSize);
        Assert.Equal(Modified, file.LastModified);
    }

    [Fact]
    public async Task WhenStorageHasNoMetadata_ThenFileKeepsItsValues()
    {
        // Arrange
        var storage = new Mock<IStorageContainer>();
        var file = new GenericFile(storage.Object, "/Data/Gone.pdf") { FileSize = 7 };

        // Act
        await file.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal(7, file.FileSize);
    }
}
