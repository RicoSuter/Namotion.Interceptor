using HomeBlaze.Abstractions.Storage;
using Moq;

namespace HomeBlaze.Storage.Tests;

public class JsonFileTests
{
    private static IStorageContainer CreateMockStorage()
    {
        return new Mock<IStorageContainer>().Object;
    }

    [Fact]
    public void Constructor_InitializesProperties()
    {
        // Arrange
        var storage = CreateMockStorage();

        // Act
        var file = new HomeBlaze.Storage.Files.JsonFile(storage, "data/config.json");

        // Assert
        Assert.Same(storage, file.Storage);
        Assert.Equal("data/config.json", file.FullPath);
        Assert.Equal("config.json", file.Name);
    }

    [Fact]
    public void Title_ReturnsFileNameWithoutExtension()
    {
        // Arrange
        var storage = CreateMockStorage();

        // Act
        var file = new HomeBlaze.Storage.Files.JsonFile(storage, "data/my-config.json");

        // Assert
        Assert.Equal("my-config", file.Title);
    }

    [Fact]
    public void Icon_ReturnsJsonIcon()
    {
        // Arrange
        var storage = CreateMockStorage();

        // Act
        var file = new HomeBlaze.Storage.Files.JsonFile(storage, "test.json");

        // Assert
        Assert.NotNull(file.Icon);
        Assert.True(file.Icon.Contains("<path") || file.Icon.Contains("<g>"), "Icon should be a MudBlazor SVG path string");
    }
}