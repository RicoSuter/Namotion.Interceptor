using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class MetadataPathKeyTests
{
    [Theory]
    [InlineData("Channels[0]/Power", "Channels/0/Power")]
    [InlineData("Channels/0/Power", "Channels/0/Power")]
    [InlineData("Meters['Main']/Phases[2]", "Meters/Main/Phases/2")]
    [InlineData("Meters[Main]/Phases/2", "Meters/Main/Phases/2")]
    [InlineData("Firmware", "Firmware")]
    public void WhenAPathIsNormalized_ThenBothNotationsGiveTheSameKey(string path, string expectedKey)
    {
        // Act
        var key = MetadataPathKey.Normalize(path);

        // Assert
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Channels[0")]
    [InlineData("Meters['Main]")]
    public void WhenAPathIsEmptyOrCannotBeRead_ThenItHasNoKey(string path)
    {
        // Act
        var key = MetadataPathKey.Normalize(path);

        // Assert
        Assert.Null(key);
    }

    [Theory]
    [InlineData("Meters['a/b']", "Meters['a']/b")]
    [InlineData("Meters['a/b']", "Meters/a/b")]
    [InlineData("Meters['a\\']/b", "Meters['a/b']")]
    public void WhenAKeyHoldsASlashOrABackslash_ThenThePathHasAnotherKeyThanTheOtherPath(string path, string otherPath)
    {
        // Act
        var key = MetadataPathKey.Normalize(path);
        var otherKey = MetadataPathKey.Normalize(otherPath);

        // Assert
        Assert.NotNull(key);
        Assert.NotEqual(otherKey, key);
    }

    [Theory]
    [InlineData("Meters['a[1]']", "Meters/a[1]")]
    [InlineData("Meters['say \"hi\"']", "Meters/say \"hi\"")]
    [InlineData("Meters['it''s']", "Meters/it's")]
    public void WhenAKeyHoldsBracketsOrQuotes_ThenTheyAreInTheKey(string path, string expectedKey)
    {
        // Act
        var key = MetadataPathKey.Normalize(path);

        // Assert
        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public void WhenTheKeyOfAPropertyIsBuilt_ThenItIsTheKeyOfThePathOfTheProperty()
    {
        // Arrange
        var subjectKey = MetadataPathKey.Normalize("Meters['a/b']")!;

        // Act
        var key = MetadataPathKey.GetPropertyKey(subjectKey, "Power");

        // Assert
        Assert.Equal(MetadataPathKey.Normalize("Meters['a/b']/Power"), key);
        Assert.NotEqual(MetadataPathKey.Normalize("Meters['a']/b/Power"), key);
    }
}
