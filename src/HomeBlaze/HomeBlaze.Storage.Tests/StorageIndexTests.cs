using HomeBlaze.Storage.Internal;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class StorageIndexTests
{
    [Theory]
    [InlineData("/Docs/Readme.md", "Docs/Readme.md")]
    [InlineData("Docs\\Readme.md", "Docs/Readme.md")]
    [InlineData("/Docs/", "Docs")]
    [InlineData("Readme.md", "Readme.md")]
    public void WhenPathIsNormalized_ThenItHasForwardSlashesAndNoOuterSlash(string path, string expected)
    {
        // Act
        var normalized = StoragePath.Normalize(path);

        // Assert
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("Docs/Guides/Setup.md", "Docs/Guides", "Setup.md", 2)]
    [InlineData("Readme.md", "", "Readme.md", 0)]
    public void WhenPathIsSplit_ThenParentNameAndDepthMatch(string path, string parent, string name, int depth)
    {
        // Act & Assert
        Assert.Equal(parent, StoragePath.GetParent(path));
        Assert.Equal(name, StoragePath.GetName(path));
        Assert.Equal(depth, StoragePath.GetDepth(path));
    }

    [Fact]
    public void WhenPathsDifferOnlyInCasing_ThenIndexKeepsBoth()
    {
        // Arrange
        var index = new StorageIndex();

        // Act
        index.Set(new StorageEntry { Path = "README.md", IsFolder = false });
        index.Set(new StorageEntry { Path = "readme.md", IsFolder = false });

        // Assert
        Assert.Equal(2, index.Count);
        Assert.True(index.TryGet("README.md", out _));
        Assert.False(index.TryGet("Readme.md", out _));
    }

    [Fact]
    public void WhenSubjectIsInIndex_ThenItsPathIsFound()
    {
        // Arrange
        var index = new StorageIndex();
        var subject = new Mock<IInterceptorSubject>().Object;
        index.Set(new StorageEntry { Path = "Docs/Motor.json", IsFolder = false, Subject = subject });

        // Act
        var found = index.TryGetPath(subject, out var path);

        // Assert
        Assert.True(found);
        Assert.Equal("Docs/Motor.json", path);
        Assert.False(index.TryGetPath(new Mock<IInterceptorSubject>().Object, out _));
    }

    [Fact]
    public void WhenFoldersAreEnsured_ThenMissingAncestorsAreAddedAndExistingOnesKept()
    {
        // Arrange
        var index = new StorageIndex();
        var docs = new StorageEntry { Path = "Docs", IsFolder = true, State = StorageEntryState.Placed };
        index.Set(docs);

        // Act
        index.EnsureFolders("Docs/Guides/Setup.md");

        // Assert
        Assert.True(index.TryGet("Docs", out var keptDocs));
        Assert.Same(docs, keptDocs);
        Assert.True(index.TryGet("Docs/Guides", out var guides));
        Assert.True(guides.IsFolder);
        Assert.False(index.TryGet("Docs/Guides/Setup.md", out _));
    }

    [Fact]
    public void WhenSameBytesAreHashed_ThenHashIsEqual()
    {
        // Act
        var first = StorageHash.Compute("content"u8);
        var second = StorageHash.Compute("content"u8);
        var other = StorageHash.Compute("Content"u8);

        // Assert
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task WhenStreamIsHashed_ThenHashEqualsTheHashOfItsBytes()
    {
        // Arrange
        var content = "content"u8.ToArray();
        using var stream = new MemoryStream(content);

        // Act
        var fromStream = await StorageHash.ComputeAsync(stream, CancellationToken.None);

        // Assert
        Assert.Equal(StorageHash.Compute(content), fromStream);
    }
}
