using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Metadata;

namespace HomeBlaze.Storage.Tests.Metadata;

public class StorageMetadataPathTests
{
    [Theory]
    [InlineData(".metadata.json", true)]
    [InlineData("Docs/.metadata.json", true)]
    [InlineData("Docs/Motor.metadata.json", true)]
    [InlineData("Docs/Motor.Metadata.JSON", false)]
    [InlineData("Docs/Motor.Metadata.json", false)]
    [InlineData("Docs/Motor.json", false)]
    [InlineData("Docs/metadata.json", false)]
    public void WhenPathIsChecked_ThenOnlyMetadataFilesAreMetadata(string path, bool expected)
    {
        // Act
        var isMetadata = StorageMetadataPath.IsMetadataFile(path);

        // Assert
        Assert.Equal(expected, isMetadata);
    }

    [Theory]
    [InlineData("", ".metadata.json")]
    [InlineData("Docs", "Docs/.metadata.json")]
    [InlineData("Docs/Guides", "Docs/Guides/.metadata.json")]
    public void WhenFolderIsGiven_ThenItsMetadataFileIsInside(string folder, string expected)
    {
        // Act
        var path = StorageMetadataPath.GetFolderFilePath(folder);

        // Assert
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("Motor.json", "Motor.metadata.json")]
    [InlineData("Devices/Motor.json", "Devices/Motor.metadata.json")]
    [InlineData("Devices/Motor.JSON", "Devices/Motor.metadata.json")]
    public void WhenSubjectFileIsGiven_ThenItsMetadataFileIsBesideIt(string subjectPath, string expected)
    {
        // Act
        var path = StorageMetadataPath.GetSubjectFilePath(subjectPath);

        // Assert
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("Motor.metadata.json", true)]
    [InlineData("Docs/Motor.Metadata.JSON", true)]
    [InlineData(".json", true)]
    [InlineData("Docs/.JSON", true)]
    [InlineData("Docs/Motor.json", false)]
    [InlineData("Docs/a.json", false)]
    public void WhenSubjectPathIsChecked_ThenNamesOfMetadataFilesAndAnEmptyNameAreReserved(string path, bool expected)
    {
        // Act
        var isReserved = StorageMetadataPath.IsReservedSubjectPath(path);

        // Assert
        Assert.Equal(expected, isReserved);
    }

    [Theory]
    [InlineData(".json", false)]
    [InlineData("Docs/.json", false)]
    [InlineData("Docs/Motor.json", true)]
    public void WhenTheNameOfAJsonFileIsEmpty_ThenItHasNoSidecarAsThatWouldBeTheFileOfItsFolder(string path, bool expected)
    {
        // Arrange
        var entry = new StorageEntry { Path = path, IsFolder = false };

        // Act
        var hasSidecar = entry.HasSidecar;

        // Assert
        Assert.Equal(expected, hasSidecar);
    }
}
