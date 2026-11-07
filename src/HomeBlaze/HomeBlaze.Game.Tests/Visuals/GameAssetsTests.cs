namespace HomeBlaze.Game.Tests.Visuals;

public class GameAssetsTests
{
    [Fact]
    public void WhenAnAssetUrlIsCreated_ThenItPointsIntoTheContentOfTheTypesAssembly()
    {
        // Act
        var gameUrl = GameAssets.Url(typeof(Game), "textures/grass.png");
        var testUrl = GameAssets.Url(typeof(GameAssetsTests), "a/b.glb");

        // Assert
        Assert.Equal("_content/HomeBlaze.Game/textures/grass.png", gameUrl);
        Assert.Equal("_content/HomeBlaze.Game.Tests/a/b.glb", testUrl);
    }
}
