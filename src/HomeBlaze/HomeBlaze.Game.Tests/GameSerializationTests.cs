using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Game.Tests;

public class GameSerializationTests
{
    private readonly ConfigurableSubjectSerializer _serializer;

    public GameSerializationTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Game), typeof(Wall), typeof(Door), typeof(GamePlayer), typeof(WallTool), typeof(Tree), typeof(TreeTool)]);
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
              "regions": {
                "-1_0": {
                  "objects": {
                    "wall-1": { "$type": "HomeBlaze.Game.Buildings.Wall", "x": -20, "y": 8, "width": 19, "depth": 1, "height": 10 }
                  }
                }
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
        var wall = Assert.IsType<Wall>(game.GetObjects()["wall-1"]);
        Assert.Equal((-20, 8, 19, 1, 10), (wall.X, wall.Y, wall.Width, wall.Depth, wall.Height));
        var player = game.Players["kitchen"];
        Assert.Equal((3, -2, GameDirection.East), (player.X, player.Y, player.Facing));
        Assert.Equal("kitchen", player.ClientId);
    }

    [Fact]
    public void WhenGameIsSerialized_ThenWallsCarryTheirType()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Regions = GameRegion.CreateRegions(new Dictionary<string, GameObject> { ["wall-1"] = new Wall { X = 1, Y = 2 } })
        };

        // Act
        var json = _serializer.Serialize(game);

        // Assert
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Buildings.Wall\"", json);
        Assert.Contains("\"width\": 4", json);
    }

    [Fact]
    public void WhenPlayerInFileHasASize_ThenThePlayerKeepsItsFixedSize()
    {
        // Arrange
        const string json = """
            {
              "$type": "HomeBlaze.Game.Game",
              "players": {
                "kitchen": { "x": 3, "y": -2, "width": 5, "depth": 6, "height": 9, "facing": "East" }
              }
            }
            """;

        // Act
        var game = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        var player = game.Players["kitchen"];
        Assert.Equal((3, -2, GameDirection.East), (player.X, player.Y, player.Facing));
        Assert.Equal((GamePlayer.Size, GamePlayer.Size, GamePlayer.BodyHeight), (player.Width, player.Depth, player.Height));
    }

    [Fact]
    public void WhenGameIsSerialized_ThenPlayersHaveNoSize()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Players = new Dictionary<string, GamePlayer> { ["kitchen"] = new GamePlayer { X = 3, Y = -2 } }
        };

        // Act
        var json = _serializer.Serialize(game);

        // Assert
        Assert.Contains("\"x\": 3", json);
        Assert.DoesNotContain("\"width\"", json);
        Assert.DoesNotContain("\"depth\"", json);
        Assert.DoesNotContain("\"height\"", json);
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
        Assert.False(player.IsMovementBlocking);
        Assert.False(player.IsVisible);
        Assert.True(wall.IsMovementBlocking);
    }

    [Fact]
    public void WhenGameWithDoorRoundTrips_ThenTheDoorKeepsItsTypeFootprintAndState()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Regions = GameRegion.CreateRegions(new Dictionary<string, GameObject> { ["door-1"] = new Door { X = 3, Y = -4, Width = 1, Depth = 4, IsOpen = true } })
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Buildings.Door\"", json);
        var door = Assert.IsType<Door>(loaded.GetObjects()["door-1"]);
        Assert.Equal((3, -4, 1, 4, 10, true), (door.X, door.Y, door.Width, door.Depth, door.Height, door.IsOpen));
    }

    [Fact]
    public void WhenAWallWithAMaterialRoundTrips_ThenItKeepsTheMaterial()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Regions = GameRegion.CreateRegions(new Dictionary<string, GameObject> { ["wall-1"] = new Wall { Material = WallMaterial.Concrete } })
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"material\": \"Concrete\"", json);
        Assert.Equal(WallMaterial.Concrete, Assert.IsType<Wall>(loaded.GetObjects()["wall-1"]).Material);
    }

    [Fact]
    public void WhenADoorWithAMaterialRoundTrips_ThenItKeepsTheMaterial()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Regions = GameRegion.CreateRegions(new Dictionary<string, GameObject> { ["door-1"] = new Door { Material = WallMaterial.Wood } })
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"material\": \"Wood\"", json);
        Assert.Equal(WallMaterial.Wood, Assert.IsType<Door>(loaded.GetObjects()["door-1"]).Material);
    }

    [Fact]
    public void WhenToolsRoundTrip_ThenTheyKeepTheirType()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Tools = new Dictionary<string, GameTool> { ["wall-tool"] = new WallTool() }
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Buildings.WallTool\"", json);
        Assert.IsType<WallTool>(loaded.Tools["wall-tool"]);
    }

    [Fact]
    public void WhenAGameWithoutToolsIsLoaded_ThenItHasNone()
    {
        // Act
        var game = Assert.IsType<Game>(_serializer.Deserialize("""{ "$type": "HomeBlaze.Game.Game", "tools": null }"""));

        // Assert
        Assert.Empty(game.Tools);
    }

    [Fact]
    public void WhenATreeAndTheTreeToolRoundTrip_ThenTheyKeepTheirTypeAndState()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Regions = GameRegion.CreateRegions(new Dictionary<string, GameObject> { ["tree-1"] = new Tree { X = 3, Y = 4, Variant = 5, Rotation = 120, Scale = 90 } }),
            Tools = new Dictionary<string, GameTool> { ["tree-tool"] = new TreeTool() }
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Nature.Tree\"", json);
        Assert.Contains("\"$type\": \"HomeBlaze.Game.Nature.TreeTool\"", json);
        var tree = Assert.IsType<Tree>(loaded.GetObjects()["tree-1"]);
        Assert.Equal((3, 4, 5, 120, 90), (tree.X, tree.Y, tree.Variant, tree.Rotation, tree.Scale));
        Assert.IsType<TreeTool>(loaded.Tools["tree-tool"]);
    }

    [Fact]
    public void WhenAPlayerWithAPersonAndColorRoundTrips_ThenItKeepsBothAndNoAnimationIsSaved()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Players = new Dictionary<string, GamePlayer>
            {
                ["kitchen"] = new GamePlayer { Character = "female-casual", Color = "#aa5500", Animation = "Clapping" }
            }
        };

        // Act
        var json = _serializer.Serialize(game);
        var loaded = Assert.IsType<Game>(_serializer.Deserialize(json));

        // Assert
        var player = loaded.Players["kitchen"];
        Assert.Equal(("female-casual", "#aa5500"), (player.Character, player.Color));
        Assert.Null(player.Animation);
        Assert.DoesNotContain("animation", json);
    }

    [Fact]
    public void WhenAPlayerHasItsVoiceOn_ThenTheTogglesAreNotSaved()
    {
        // Arrange
        var game = new Game(Microsoft.Extensions.Logging.Abstractions.NullLogger<Game>.Instance)
        {
            Players = new Dictionary<string, GamePlayer> { ["kitchen"] = new GamePlayer { IsMicrophoneOn = true, IsAudioOn = true } }
        };

        // Act
        var json = _serializer.Serialize(game);

        // Assert
        Assert.DoesNotContain("isMicrophoneOn", json);
        Assert.DoesNotContain("isAudioOn", json);
    }
}
