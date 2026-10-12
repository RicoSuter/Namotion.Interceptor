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
    [InlineData("Home.md~")]
    [InlineData("~Draft.md")]
    [InlineData("Notes.md.tmp")]
    [InlineData("Notes.tmp.md")]
    [InlineData("Docs/Notes.md.TMP")]
    [InlineData("/Build.tmp/Output.md")]
    [InlineData("Docs/~Backup/Notes.md")]
    [InlineData("Docs/Old~/Notes.md")]
    public void WhenPathHasHiddenOrTemporarySegment_ThenItIsIgnored(string path)
    {
        // Act
        var isIgnored = StoragePathFilter.IsIgnored(path);

        // Assert
        Assert.True(isIgnored);
    }

    [Theory]
    [InlineData("Home.md")]
    [InlineData("/Docs/Readme.md")]
    [InlineData("Docs/v1.2/Notes.md")]
    [InlineData("file.")]
    [InlineData("Docs/tmp/Notes.md")]
    [InlineData("Docs/a~b/Notes.md")]
    [InlineData("")]
    public void WhenNoSegmentIsHiddenOrTemporary_ThenItIsNotIgnored(string path)
    {
        // Act
        var isIgnored = StoragePathFilter.IsIgnored(path);

        // Assert
        Assert.False(isIgnored);
    }
}
