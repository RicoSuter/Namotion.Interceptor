using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Game.Tests;

public class GameSerializationTests
{
    private readonly ConfigurableSubjectSerializer _serializer;

    public GameSerializationTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Game), typeof(Wall), typeof(GamePlayer)]);
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        _serializer = new ConfigurableSubjectSerializer(typeProvider, services);
    }

    [Fact]
    public void WhenGameWithWallIsDeserialized_ThenTheWallKeepsItsFootprint()
    {
        // Arrange
        const string json = """
            {
              "$type": "HomeBlaze.Game.Game",
              "name": "Demo world",
              "objects": {
                "wall-1": { "$type": "HomeBlaze.Game.Wall", "x": -20, "y": 8, "width": 19, "depth": 1, "height": 10 }
              },
              "players": {
                "kitchen": { "x": 3, "y": -2, "facing": "East" }
              }
            }
            """;

        // Act
        var game = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Equal("Demo world", game.Name);
        var wall = Assert.IsType<Wall>(game.Objects["wall-1"]);
        Assert.Equal((-20, 8, 19, 1, 10), (wall.X, wall.Y, wall.Width, wall.Depth, wall.Height));
        var player = game.Players["kitchen"];
        Assert.Equal((3, -2, GameDirection.East), (player.X, player.Y, player.Facing));
        Assert.Equal("kitchen", player.ClientId);
    }

    [Fact]
    public void WhenGameIsSerialized_ThenWallsCarryTheirType()
    {
        // Arrange
        var game = new Game
        {
            Objects = new Dictionary<string, GameObject> { ["wall-1"] = new Wall { X = 1, Y = 2 } }
        };

        // Act
        var json = _serializer.Serialize(game);

        // Assert
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Wall\"", json);
        Assert.Contains("\"width\": 4", json);
    }

    [Fact]
    public void WhenObjectsAreCreated_ThenTheyHaveTheirDefaultSizes()
    {
        // Act
        var wall = new Wall();
        var player = new GamePlayer();

        // Assert
        Assert.Equal((4, 1, 10), (wall.Width, wall.Depth, wall.Height));
        Assert.Equal((2, 2, 7), (player.Width, player.Depth, player.Height));
        Assert.Equal(GameDirection.North, player.Facing);
        Assert.False(player.IsBlocking);
        Assert.False(player.IsVisible);
        Assert.True(wall.IsBlocking);
    }
}
