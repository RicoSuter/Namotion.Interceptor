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
}
