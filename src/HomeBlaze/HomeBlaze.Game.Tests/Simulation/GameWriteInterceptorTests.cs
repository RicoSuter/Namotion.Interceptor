using System.Reactive.Concurrency;
using HomeBlaze.Game.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameWriteInterceptorTests
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly GameTestContext _context = new();

    [Fact]
    public void WhenADoorIsOpenedFromOutside_ThenTheIndexSeesItAfterOneTick()
    {
        // Arrange
        var door = _context.AddDoor(10, 10);
        var isFreeBefore = _context.Game.IsFree(door.Area);

        // Act
        door.IsOpen = true;
        var isFreeBeforeTick = _context.Game.IsFree(door.Area);
        _context.Tick();

        // Assert
        Assert.False(isFreeBefore);
        Assert.False(isFreeBeforeTick);
        Assert.True(_context.Game.IsFree(door.Area));
    }

    [Theory]
    [InlineData(nameof(GamePlayer.ClientId), "other")]
    [InlineData(nameof(GamePlayer.ClientTitle), "Other")]
    [InlineData(nameof(GamePlayer.IsConnected), false)]
    [InlineData(nameof(GamePlayer.IsMicrophoneOn), true)]
    [InlineData(nameof(GamePlayer.IsAudioOn), true)]
    [InlineData(nameof(GamePlayer.Animation), "Clapping")]
    public void WhenAStateOfAPlayerIsWrittenThroughTheRegistry_ThenTheWriteIsRefused(string name, object value)
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var property = player.TryGetRegisteredSubject()!.TryGetProperty(name)!;
        var before = property.GetValue();

        // Act
        var exception = Record.Exception(() => property.SetValue(value));

        // Assert
        Assert.Equal($"Only the game sets {name}.", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Equal(before, property.GetValue());
    }

    [Fact]
    public async Task WhenObjectsAreReplacedFromOutsideBeforeAPlaceIsQueued_ThenBothAreKeptAndPublished()
    {
        // Arrange
        var replaced = new Wall { X = 10, Y = 10 };
        var placed = new Wall { X = 20, Y = 20 };

        // Act
        _context.Game.SetObjects(new Dictionary<string, GameObject> { ["replaced"] = replaced });
        var place = _context.Game.PlaceAsync(placed, CancellationToken.None);
        await _context.RunAsync(place);
        _context.Publish();

        // Assert
        var objects = _context.Game.GetObjects();
        Assert.Equal(2, objects.Count);
        Assert.Same(replaced, objects["replaced"]);
        Assert.Contains(placed, objects.Values);
    }

    [Fact]
    public async Task WhenObjectsAreReplacedWhileATickPublishesAnOlderSet_ThenTheReplacementWins()
    {
        // Arrange
        var replacement = new Wall { X = 30, Y = 30 };

        // Lands while the place reads the object, before the publish of the same tick, which writes the set without it.
        var placed = new ProbeObject
        {
            X = 20,
            Y = 20,
            OnFirstRead = () => _context.Game.SetObjects(new Dictionary<string, GameObject> { ["replacement"] = replacement })
        };
        _context.Publish();

        // Act
        await _context.RunAsync(_context.Game.PlaceAsync(placed, CancellationToken.None));
        var isOverwrittenByThePublish = !_context.Game.GetObjects().ContainsKey("replacement");
        _context.Publish();

        // Assert
        Assert.True(isOverwrittenByThePublish);
        Assert.Same(replacement, Assert.Single(_context.Game.GetObjects()).Value);
        Assert.True(_context.Game.IsFree(placed.Area));
        Assert.False(_context.Game.IsFree(replacement.Area));
    }

    [Fact]
    public async Task WhenThePositionOfAPlayerIsWrittenFromOutside_ThenThePlayerIsThereAtTheNextTick()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        player.X = 30;
        player.Y = -4;
        _context.Tick();
        var result = await _context.StepAsync("tab-1", GameDirection.East);

        // Assert
        Assert.Equal(new MoveResult(true, 31, -4, GameDirection.East), result);
    }

    [Fact]
    public async Task WhenASynchronousObserverWritesAnObject_ThenTheWriteCompletesWithoutATick()
    {
        // Arrange
        var door = _context.AddDoor(10, 10);
        var wall = _context.AddWall(30, 0);
        using var subscription = ((IInterceptorSubject)_context.Root).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, door) && change.Property.Name == nameof(Door.IsOpen))
                {
                    wall.X = 40;
                }
            });

        // Act
        // Times out instead of hanging when the write waits for a tick, which only this thread runs.
        await Task.Run(() => door.IsOpen = true).WaitAsync(WriteTimeout);
        var xBeforeTick = wall.X;
        _context.Tick();

        // Assert
        Assert.Equal(40, xBeforeTick);
        Assert.True(_context.Game.IsFree(new GameArea(30, 0, 4, 1)));
        Assert.False(_context.Game.IsFree(wall.Area));
    }
}
