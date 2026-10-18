using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

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
}
