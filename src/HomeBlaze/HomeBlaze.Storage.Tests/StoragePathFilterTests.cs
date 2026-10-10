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

    [Theory]
    [InlineData("Home.md~")]
    [InlineData("~Draft.md")]
    [InlineData("Notes.md.tmp")]
    [InlineData("Notes.tmp.md")]
    [InlineData("/Build.tmp/Output.md")]
    [InlineData("Docs/~Backup/Notes.md")]
    [InlineData("Docs\\Old~\\Notes.md")]
    public void WhenPathHasSegmentWithTemporaryName_ThenItHasTemporarySegment(string path)
    {
        // Act
        var hasTemporarySegment = StoragePathFilter.HasTemporarySegment(path);

        // Assert
        Assert.True(hasTemporarySegment);
    }

    [Theory]
    [InlineData("Home.md")]
    [InlineData("/Docs/Readme.md")]
    [InlineData("Docs/tmp/Notes.md")]
    [InlineData("Docs/a~b/Notes.md")]
    [InlineData("")]
    public void WhenNoSegmentHasTemporaryName_ThenItHasNoTemporarySegment(string path)
    {
        // Act
        var hasTemporarySegment = StoragePathFilter.HasTemporarySegment(path);

        // Assert
        Assert.False(hasTemporarySegment);
    }

    [Theory]
    [InlineData(".idea/workspace.xml", true)]
    [InlineData("Docs/Notes.md.tmp", true)]
    [InlineData("Build.tmp/Output.md", true)]
    [InlineData("Docs/Readme.md", false)]
    public void WhenPathIsHiddenOrTemporary_ThenItIsIgnored(string path, bool expected)
    {
        // Act
        var isIgnored = StoragePathFilter.IsIgnored(path);

        // Assert
        Assert.Equal(expected, isIgnored);
    }
}
