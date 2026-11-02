using HomeBlaze.Game.Clients;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameWorldStateTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly GameWorldState _world;

    public GameWorldStateTests()
    {
        _world = new GameWorldState(_timeProvider, new GameVoiceMixer(_timeProvider, NullLogger.Instance));
    }

    private PlayerEntity Join(string clientId, int x = 0, int y = 0)
    {
        _world.Reconcile([new GameClient(clientId, clientId, IsNamed: false, IsConnected: true)]);
        var player = _world.Players[clientId];
        _world.Teleport(player.Subject, x, y);
        return player;
    }

    private List<GameObject> TakeOverObjects(Dictionary<string, GameObject> objects) =>
        _world.TakeOverRegions(GameRegion.CreateRegions(objects));

    // The objects whose OnAddedToWorld a publish of the object set makes due.
    private List<GameObject> PublishNewObjects()
    {
        _world.BeginObjectSetPublish();
        _world.EndObjectSetPublish(isWritten: true);
        var published = new List<GameObject>();
        _world.MovePublishedNewObjectsTo(published);
        return published;
    }

    private MoveResult Step(PlayerEntity player, GameDirection direction)
    {
        _timeProvider.Advance(Game.StepInterval);
        return _world.TryMove(player, direction, turnOnly: false);
    }

    [Fact]
    public void WhenOverlappingObjectsAreTakenOver_ThenAllOfThemAreKept()
    {
        // Arrange
        var first = new Wall { X = 0, Y = 0 };
        var second = new Wall { X = 2, Y = 0 };

        // Act
        var removed = TakeOverObjects(new Dictionary<string, GameObject> { ["first"] = first, ["second"] = second });

        // Assert
        Assert.Equal(2, _world.Objects.Count);
        Assert.Equal(2, _world.Index.Count);
        Assert.Equal(new GameObject[] { first, second }, PublishNewObjects());
        Assert.Empty(removed);
    }

    [Fact]
    public void WhenObjectsAreTakenOverAgain_ThenStayingObjectsKeepTheirEntitiesAndLeavingOnesAreRemoved()
    {
        // Arrange
        var staying = new Wall { X = 0, Y = 0 };
        var leaving = new Wall { X = 10, Y = 0 };
        TakeOverObjects(new Dictionary<string, GameObject> { ["staying"] = staying, ["leaving"] = leaving });
        PublishNewObjects();
        var entity = _world.EntitiesBySubject[staying];

        // Act
        var removed = TakeOverObjects(new Dictionary<string, GameObject> { ["staying"] = staying });

        // Assert
        Assert.Same(entity, _world.EntitiesBySubject[staying]);
        Assert.Empty(PublishNewObjects());
        Assert.Equal(new GameObject[] { leaving }, removed);
        Assert.False(_world.EntitiesBySubject.ContainsKey(leaving));
        Assert.Equal(1, _world.Index.Count);
    }

    [Fact]
    public void WhenAPlayerJoinsOnABlockedPosition_ThenItSpawnsOnTheNearestFreePosition()
    {
        // Arrange
        TakeOverObjects(new Dictionary<string, GameObject> { ["wall"] = new Wall { X = -1, Y = 0 } });

        // Act
        var joined = _world.Reconcile([new GameClient("tab-1", "Tab", IsNamed: false, IsConnected: true)]);

        // Assert
        var player = Assert.Single(joined!);
        Assert.Equal("tab-1", player.Subject.ClientId);
        Assert.True(player.IsConnected);
        Assert.False(player.Area.Intersects(new GameArea(-1, 0, 4, 1)));
        Assert.True(Math.Abs(player.Area.X) <= 2 && Math.Abs(player.Area.Y) <= 2);
        Assert.True(_world.IsPlayerSetChanged);
    }

    [Fact]
    public void WhenAStepGoesIntoAWall_ThenItIsRefusedButThePlayerTurns()
    {
        // Arrange
        var player = Join("tab-1");
        TakeOverObjects(new Dictionary<string, GameObject> { ["wall"] = new Wall { X = 1, Y = 2, Width = 1 } });

        // Act
        var result = Step(player, GameDirection.North);

        // Assert
        Assert.Equal(new MoveResult(false, 0, 0, GameDirection.North), result);
        Assert.Equal(GameDirection.North, player.Facing);
    }

    [Fact]
    public void WhenAPlayerStandsInAClosedDoor_ThenItCanStepOutOfIt()
    {
        // Arrange
        // The door closed by an automation while the player stood in it.
        var player = Join("tab-1", y: 2);
        TakeOverObjects(new Dictionary<string, GameObject> { ["door"] = new Door { X = -1, Y = 2, Depth = 2 } });

        // Act
        var result = Step(player, GameDirection.North);

        // Assert
        Assert.True(result.IsAccepted);
        Assert.Equal((0, 3), (player.Area.X, player.Area.Y));
    }

    [Fact]
    public void WhenAPlayerStepsIntoAClosedDoorBesideIt_ThenTheStepIsRefused()
    {
        // Arrange
        var player = Join("tab-1");
        TakeOverObjects(new Dictionary<string, GameObject> { ["door"] = new Door { X = -1, Y = 2 } });

        // Act
        var result = Step(player, GameDirection.North);

        // Assert
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public void WhenAnObjectIsPlacedOntoABlockingObject_ThenItIsRefused()
    {
        // Arrange
        TakeOverObjects(new Dictionary<string, GameObject> { ["wall"] = new Wall { X = 0, Y = 4, Width = 1 } });

        // Act
        var exception = Record.Exception(() => _world.Place(new Wall { X = 0, Y = 2, Width = 1, Depth = 4 }));

        // Assert
        Assert.Equal("Something is in the way.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Single(_world.Objects);
    }

    [Fact]
    public void WhenAReplacementIsRefused_ThenNothingChanges()
    {
        // Arrange
        var wall = new Wall { X = 0, Y = 10, Width = 10 };
        var other = new Wall { X = 12, Y = 10 };
        TakeOverObjects(new Dictionary<string, GameObject> { ["wall"] = wall, ["other"] = other });
        _world.Regions.OnPublished(_world.Regions.ChangeCount);
        var fitting = new Wall { X = 0, Y = 10, Width = 3 };

        // Act
        var exception = Record.Exception(() => _world.Replace(wall, [fitting, new Wall { X = 5, Y = 10, Width = 10 }]));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(new[] { "other", "wall" }, _world.Objects.Keys.Order());
        Assert.False(_world.EntitiesBySubject.ContainsKey(fitting));
        Assert.Equal(2, _world.Index.Count);
        Assert.False(_world.IsObjectSetChanged);
    }

    [Fact]
    public void WhenAnObjectIsReplaced_ThenTheReplacementsTakeItsPlaceInOneStep()
    {
        // Arrange
        var wall = new Wall { X = 0, Y = 10, Width = 10 };
        TakeOverObjects(new Dictionary<string, GameObject> { ["wall"] = wall });
        var west = new Wall { X = 0, Y = 10, Width = 3 };
        var door = new Door { X = 3, Y = 10, Width = 4 };
        var east = new Wall { X = 7, Y = 10, Width = 3 };

        // Act
        _world.Replace(wall, [west, door, east]);

        // Assert
        Assert.Equal(3, _world.Objects.Count);
        Assert.False(_world.EntitiesBySubject.ContainsKey(wall));
        Assert.All(new GameObject[] { west, door, east }, replacement => Assert.True(_world.NamesBySubject.ContainsKey(replacement)));
        Assert.Equal(3, _world.Index.Count);
        Assert.True(_world.IsSaveRequested);
    }

    [Fact]
    public void WhenADoorIsOpenedByADirectWrite_ThenTheRefreshUnblocksItsArea()
    {
        // Arrange
        var door = new Door { X = 0, Y = 0 };
        TakeOverObjects(new Dictionary<string, GameObject> { ["door"] = door });
        var isBlockedBefore = _world.Index.IsBlocked(door.Area, ignored: null);

        // Act
        door.IsOpen = true;
        _world.ChangedSubjects.Add(door);
        _world.RefreshChangedSubjects();

        // Assert
        Assert.True(isBlockedBefore);
        Assert.False(_world.Index.IsBlocked(door.Area, ignored: null));
        Assert.Empty(_world.ChangedSubjects);
        Assert.True(_world.IsSaveRequested);
    }

    [Fact]
    public void WhenTheSnapshotIsPublished_ThenItShowsTheBlockingAreasAndPlayerPositions()
    {
        // Arrange
        var player = Join("tab-1", x: 5, y: 6);
        var wall = new Wall { X = 20, Y = 0 };
        TakeOverObjects(new Dictionary<string, GameObject> { ["wall"] = wall });

        // Act
        _world.PublishSnapshot();

        // Assert
        Assert.False(_world.Snapshot.IsFree(wall.Area, except: null));
        Assert.True(_world.Snapshot.IsFree(wall.Area, wall));
        var position = _world.PlayerPositions["tab-1"];
        Assert.Equal((player.Subject, new GameArea(5, 6, 2, 2), true), (position.Subject, position.Area, position.IsConnected));
    }

    [Fact]
    public void WhenPlayersAreTakenOverAfterARestart_ThenKeptPlayersTakeTheirSubjectsAppearance()
    {
        // Arrange
        var player = Join("tab-1");
        player.Subject.Character = "male-shirt";
        player.Subject.Color = "#112233";
        var players = new Dictionary<string, GamePlayer> { ["tab-1"] = player.Subject };

        // Act
        _world.TakeOverPlayers(players, isRestart: true);

        // Assert
        Assert.Equal(("male-shirt", "#112233"), (player.Character, player.Color));
    }

    [Fact]
    public void WhenAnAppearanceWriteResolvesToTheSameValue_ThenASaveIsStillRequested()
    {
        // Arrange
        var player = Join("tab-1");
        player.Subject.Character = "robot";
        _world.IsSaveRequested = false;

        // Act
        _world.RefreshAppearance(player.Subject);

        // Assert
        Assert.True(_world.IsSaveRequested);
    }

    [Fact]
    public void WhenPlayersAreTakenOverAfterARestart_ThenKeptPlayersTakeTheirSubjectsPositionWithTheirVoiceOff()
    {
        // Arrange
        var player = Join("tab-1");
        player.MicrophonePageId = 1;
        player.IsMicrophoneOn = true;
        player.AudioPageIds = [1, 2];
        player.IsAudioOn = true;
        player.Subject.X = 30;
        var players = new Dictionary<string, GamePlayer> { ["tab-1"] = player.Subject };

        // Act
        _world.TakeOverPlayers(players, isRestart: true);

        // Assert
        Assert.Same(player, _world.Players["tab-1"]);
        Assert.Equal(30, player.Area.X);
        Assert.Null(player.MicrophonePageId);
        Assert.Empty(player.AudioPageIds);
        Assert.False(player.IsMicrophoneOn || player.IsAudioOn);
        Assert.True(_world.IsVoiceChanged);
    }

    [Fact]
    public void WhenPlayersAreTakenOverWithoutARestart_ThenKeptPlayersKeepTheirVoice()
    {
        // Arrange
        var player = Join("tab-1");
        player.MicrophonePageId = 1;
        player.IsMicrophoneOn = true;

        // Act
        _world.TakeOverPlayers(new Dictionary<string, GamePlayer> { ["tab-1"] = player.Subject });

        // Assert
        Assert.Equal(1, player.MicrophonePageId);
        Assert.True(player.IsMicrophoneOn);
    }
}
