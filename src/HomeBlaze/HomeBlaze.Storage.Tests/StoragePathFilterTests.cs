using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StoragePathFilterTests
{
    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("/.DS_Store")]
    [InlineData("._Home.md")]
    [InlineData(".idea")]
    [InlineData(".idea/workspace.xml")]
    [InlineData("/Docs/.hidden.md")]
    [InlineData("Docs\\.git\\config")]
    public void WhenPathHasSegmentStartingWithDot_ThenItIsHidden(string path)
    {
        // Act
        var isHidden = StoragePathFilter.IsHidden(path);

        // Assert
        Assert.True(isHidden);
    }

    [Theory]
    [InlineData("Home.md")]
    [InlineData("/Docs/Readme.md")]
    [InlineData("Docs/v1.2/notes.tmp.md")]
    [InlineData("file.")]
    [InlineData("")]
    public void WhenNoSegmentStartsWithDot_ThenItIsNotHidden(string path)
    {
        // Act
        var isHidden = StoragePathFilter.IsHidden(path);

        // Assert
        Assert.False(isHidden);
    }
}
