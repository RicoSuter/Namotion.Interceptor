using System.Reactive.Concurrency;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;
using RecordingLogger = HomeBlaze.Game.Tests.Testing.RecordingLogger;

namespace HomeBlaze.Game.Tests;

public class GameRegionTests
{
    private readonly GameTestContext _context = new();

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(63, 64, 0, 1)]
    [InlineData(-1, -64, -1, -1)]
    [InlineData(-65, 127, -2, 1)]
    public void WhenCoordinatesAreMappedToARegion_ThenTheRegionHoldsTheTile(int x, int y, int regionX, int regionY)
    {
        // Act
        var coordinates = GameRegion.GetCoordinates(x, y);

        // Assert
        Assert.Equal((regionX, regionY), coordinates);
    }

    [Theory]
    [InlineData("0_0", true)]
    [InlineData("-1_2", true)]
    [InlineData("01_0", false)]
    [InlineData("+1_0", false)]
    [InlineData("1_", false)]
    [InlineData("_1", false)]
    [InlineData("region", false)]
    public void WhenAKeyIsParsed_ThenOnlyTheKeysOfRegionsAreAccepted(string key, bool isRegion)
    {
        // Act
        var isParsed = GameRegion.TryParseKey(key, out var regionX, out var regionY);

        // Assert
        Assert.Equal(isRegion, isParsed);
        Assert.Equal(isRegion ? key : "0_0", GameRegion.GetKey(regionX, regionY));
    }

    [Fact]
    public async Task WhenAnObjectIsPlaced_ThenOnlyItsRegionIsWritten()
    {
        // Arrange
        _context.AddWall(0, 0);
        _context.AddWall(100, 0);
        _context.Publish();
        var regions = _context.Game.Regions;
        var otherObjects = regions["1_0"].Objects;
        var wall = new Wall { X = 10, Y = 10 };

        // Act
        await _context.RunAsync(_context.Game.PlaceAsync(wall, CancellationToken.None));

        // Assert
        Assert.Same(regions, _context.Game.Regions);
        Assert.Same(otherObjects, regions["1_0"].Objects);
        Assert.Contains(wall, regions["0_0"].Objects.Values);
    }

    [Fact]
    public async Task WhenAnObjectIsPlacedWhereNoRegionIsYet_ThenTheRegionIsAddedWithIt()
    {
        // Arrange
        _context.AddWall(0, 0);
        var objects = _context.Game.Regions["0_0"].Objects;
        var wall = new Wall { X = -10, Y = -70 };

        // Act
        await _context.RunAsync(_context.Game.PlaceAsync(wall, CancellationToken.None));

        // Assert
        Assert.Same(wall, Assert.Single(_context.Game.Regions["-1_-2"].Objects).Value);
        Assert.Same(objects, _context.Game.Regions["0_0"].Objects);
        Assert.Same(_context.Game, wall.TryGetFirstParent<Game>());
    }

    [Fact]
    public async Task WhenTheLastObjectOfARegionIsRemoved_ThenTheRegionIsRemoved()
    {
        // Arrange
        _context.AddWall(0, 0);
        var wall = _context.AddWall(100, 0);

        // Act
        await _context.RunAsync(_context.Game.RemoveAsync(wall, CancellationToken.None));

        // Assert
        Assert.Equal(["0_0"], _context.Game.Regions.Keys);
        Assert.Null(wall.TryGetFirstParent<Game>());
    }

    [Fact]
    public void WhenAnObjectMovesAcrossARegionBoundary_ThenOnePublishMovesItAndItKeepsItsIdentityAndSchedules()
    {
        // Arrange
        _context.AddWall(0, 0);
        var moving = _context.AddObject("moving", new LifecycleObject { X = 62, Y = 0 });
        _context.Game.WorldForTest.Index.Changes.Clear();
        var scheduleDelay = GameSimulationSettings.TickInterval * 4;
        moving.ScheduleIncrement(scheduleDelay);
        var objectsBefore = _context.Game.Regions["0_0"].Objects;

        // Act
        moving.X = 66;
        _context.Tick();

        // Assert
        Assert.Same(moving, Assert.Single(_context.Game.Regions["1_0"].Objects).Value);
        Assert.Equal("moving", Assert.Single(_context.Game.Regions["1_0"].Objects).Key);
        Assert.DoesNotContain(moving, _context.Game.Regions["0_0"].Objects.Values);
        Assert.NotSame(objectsBefore, _context.Game.Regions["0_0"].Objects);
        Assert.Equal((1, 0, 1), (moving.AttachCount, moving.DetachCount, moving.AddedCount));
        _context.Tick(4);
        Assert.Equal(1, moving.RunCount);
    }

    [Fact]
    public void WhenTheOnlyObjectOfARegionMovesToANewRegion_ThenTheRegionsAreSwappedWithoutDetachingIt()
    {
        // Arrange
        var moving = _context.AddObject("moving", new LifecycleObject { X = 62, Y = 0 });
        var formerRegion = _context.Game.Regions["0_0"];

        // Act
        moving.X = 66;
        _context.Tick();

        // Assert
        Assert.Equal(["1_0"], _context.Game.Regions.Keys);
        Assert.Same(moving, Assert.Single(_context.Game.Regions["1_0"].Objects).Value);
        Assert.Equal((1, 0, 1), (moving.AttachCount, moving.DetachCount, moving.AddedCount));
        Assert.Null(formerRegion.TryGetFirstParent<Game>());
        Assert.Same(_context.Game, moving.TryGetFirstParent<Game>());
    }

    [Fact]
    public void WhenTwoObjectsSwapRegionsInOneTick_ThenNeitherLeavesTheGraph()
    {
        // Arrange
        var east = _context.AddObject("east", new LifecycleObject { X = 66, Y = 0 });
        var west = _context.AddObject("west", new LifecycleObject { X = 62, Y = 10 });

        // Act
        east.X = 60;
        west.X = 70;
        _context.Tick();

        // Assert
        Assert.Same(east, Assert.Single(_context.Game.Regions["0_0"].Objects).Value);
        Assert.Same(west, Assert.Single(_context.Game.Regions["1_0"].Objects).Value);
        Assert.Equal((1, 0), (east.AttachCount, east.DetachCount));
        Assert.Equal((1, 0), (west.AttachCount, west.DetachCount));
    }

    [Fact]
    public void WhenARegionIsWrittenFromOutside_ThenItsObjectsAreTakenOverAndOtherRegionsStay()
    {
        // Arrange
        var replaced = _context.AddWall(0, 0);
        _context.AddWall(100, 0);
        var otherObjects = _context.Game.Regions["1_0"].Objects;
        var added = new LifecycleObject { X = 10, Y = 10 };

        // Act
        _context.Game.Regions["0_0"].Objects = new Dictionary<string, GameObject> { ["added"] = added };
        _context.Tick();

        // Assert
        Assert.True(_context.Game.IsFree(replaced.Area));
        Assert.False(_context.Game.IsFree(added.Area));
        Assert.Equal(1, added.AddedCount);
        Assert.Same(added, Assert.Single(_context.Game.Regions["0_0"].Objects).Value);
        Assert.Same(otherObjects, _context.Game.Regions["1_0"].Objects);
    }

    [Fact]
    public void WhenARegionWrittenFromOutsideListsAnObjectOfAnotherRegion_ThenTheObjectMovesToTheRegionOfItsPosition()
    {
        // Arrange
        _context.AddWall(0, 0);
        var misplaced = new LifecycleObject { X = 100, Y = 0 };
        var region = _context.Game.Regions["0_0"];

        // Act
        region.Objects = new Dictionary<string, GameObject>(region.Objects) { ["misplaced"] = misplaced };
        _context.Tick();

        // Assert
        Assert.Same(misplaced, Assert.Single(_context.Game.Regions["1_0"].Objects).Value);
        Assert.DoesNotContain(misplaced, _context.Game.Regions["0_0"].Objects.Values);
        Assert.Equal((1, 0, 1), (misplaced.AttachCount, misplaced.DetachCount, misplaced.AddedCount));
    }

    [Fact]
    public void WhenARegionWrittenFromOutsideUsesTheNameOfAnObjectOfAnotherRegion_ThenTheNewObjectGetsANewName()
    {
        // Arrange
        var existing = _context.AddObject("taken", new Wall { X = 100, Y = 0 });
        var newcomer = new Wall { X = 0, Y = 0 };

        // Act
        _context.AddObject("taken", newcomer);
        _context.Publish();

        // Assert
        var objects = _context.Game.GetObjects();
        Assert.Equal(2, objects.Count);
        Assert.Same(existing, objects["taken"]);
        Assert.StartsWith("wall-", Assert.Single(_context.Game.Regions["0_0"].Objects).Key);
        Assert.False(_context.Game.IsFree(newcomer.Area));
    }

    [Fact]
    public void WhenTheRegionsAreWrittenFromOutside_ThenTheObjectsOfTheFormerRegionsLeaveTheWorld()
    {
        // Arrange
        var former = _context.AddObject("former", new ScheduledObject { X = 100, Y = 0 });
        former.ScheduleIncrement(GameSimulationSettings.TickInterval * 2);
        var wall = new Wall { X = -10, Y = 0 };

        // Act
        _context.Game.SetObjects(new Dictionary<string, GameObject> { ["wall"] = wall });
        _context.Tick(3);

        // Assert
        Assert.Equal(["-1_0"], _context.Game.Regions.Keys);
        Assert.True(_context.Game.IsFree(former.Area));
        Assert.False(_context.Game.IsFree(wall.Area));
        Assert.Equal(0, former.RunCount);
    }

    [Fact]
    public void WhenAWorldIsLoadedWithObjectsInOtherRegionsAndAStrayKey_ThenTheStartPutsEveryObjectIntoItsRegion()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Game), typeof(Wall)]);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().AddLogging().BuildServiceProvider());
        const string json = """
            {
              "$type": "HomeBlaze.Game.Game",
              "regions": {
                "0_0": {
                  "objects": {
                    "home": { "$type": "HomeBlaze.Game.Buildings.Wall", "x": 1, "y": 1 },
                    "east": { "$type": "HomeBlaze.Game.Buildings.Wall", "x": 70, "y": 1 }
                  }
                },
                "1_0": {
                  "objects": {
                    "home": { "$type": "HomeBlaze.Game.Buildings.Wall", "x": 80, "y": 1 }
                  }
                },
                "somewhere": {
                  "objects": {
                    "south": { "$type": "HomeBlaze.Game.Buildings.Wall", "x": 1, "y": -10 }
                  }
                }
              }
            }
            """;
        var loaded = Assert.IsType<Game>(serializer.Deserialize(json));
        var timeProvider = new FakeTimeProvider();
        var root = new TestRoot(SubjectContextFactory.Create(new ServiceCollection()));
        var game = new Game(new FakeClientDirectory(), timeProvider, NullLogger.Instance) { Regions = loaded.Regions };
        root.Game = game;

        // Act
        game.StartForTest();
        timeProvider.Advance(GameSimulationSettings.TickInterval);
        game.RunTickForTest();

        // Assert
        var regions = game.Regions;
        Assert.Equal(["0_-1", "0_0", "1_0"], regions.Keys.Order());
        Assert.Equal(["home"], regions["0_0"].Objects.Keys);
        Assert.Equal(2, regions["1_0"].Objects.Count);
        Assert.Contains("east", regions["1_0"].Objects.Keys);
        Assert.Equal(["south"], regions["0_-1"].Objects.Keys);
        Assert.Equal(4, game.GetObjects().Count);
        Assert.All(game.GetObjects().Values, gameObject => Assert.False(game.IsFree(gameObject.Area)));
    }

    [Theory]
    [InlineData(10, 20)]
    [InlineData(300, 0)]
    public async Task WhenAnObserverOfARegionWritePlacesAnObject_ThenThePublishGoesOnAndTheNextPublishWritesIt(int x, int y)
    {
        // Arrange
        var logger = new RecordingLogger();
        var context = new GameTestContext(logger);
        context.AddWall(0, 0);
        context.AddWall(100, 0);
        var placed = new LifecycleObject { X = x, Y = y };
        Task? place = null;
        var isFreeDuringThePublish = false;
        using var subscription = ((IInterceptorSubject)context.Root).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (place is null && change.Property.Subject is GameRegion && change.Property.Name == nameof(GameRegion.Objects))
                {
                    isFreeDuringThePublish = context.Game.IsFree(new GameArea(200, 200, 1, 1));
                    place = context.Game.PlaceAsync(placed, CancellationToken.None);
                }
            });

        // Act
        await context.RunAsync(context.Game.PlaceAsync(new Wall { X = 20, Y = 0 }, CancellationToken.None));
        context.Tick();

        // Assert
        Assert.True(isFreeDuringThePublish);
        Assert.True(place!.IsCompletedSuccessfully);
        Assert.Contains(placed, context.Game.GetObjects().Values);
        Assert.Same(context.Game, placed.TryGetFirstParent<Game>());
        Assert.Equal(1, placed.AddedCount);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public void WhenARegionWriteLandsAfterTheQueueDrainedButBeforeThePublish_ThenTheNewObjectGetsOnAddedToWorldOnceItIsAttached()
    {
        // Arrange
        _context.AddWall(0, 0);
        var region = _context.Game.Regions["0_0"];
        var added = new HookObject { X = 10, Y = 10 };
        var placed = new ProbeObject
        {
            X = 20,
            Y = 20,
            OnFirstRead = () => region.Objects = new Dictionary<string, GameObject>(region.Objects) { ["added"] = added }
        };

        // Act
        // The place reads the probe inside the tick, after the queue drained, and its publish overwrites the region.
        var place = _context.Game.PlaceAsync(placed, CancellationToken.None);
        _context.Tick();
        var isAttachedAfterThePlace = added.TryGetFirstParent<Game>() is not null;
        _context.Tick();

        // Assert
        Assert.True(place.IsCompletedSuccessfully);
        Assert.False(isAttachedAfterThePlace);
        Assert.Equal(1, added.AddedCount);
        Assert.True(added.HadWorldInHook);
        Assert.Contains(added, _context.Game.GetObjects().Values);
    }

    [Fact]
    public void WhenAScheduleMovesAnObjectAcrossARegionBoundary_ThenTheSameTickPublishesItsRegionWithoutDetachingIt()
    {
        // Arrange
        var moving = _context.AddObject("moving", new LifecycleObject { X = 62, Y = 0 });
        _context.AddWall(0, 10);
        _context.Game.Schedule(moving, TimeSpan.Zero, () => moving.X = 66);

        // Act
        for (var i = 0; i < 3 && moving.X != 66; i++)
        {
            _context.Tick();
        }

        // Assert
        Assert.Equal(66, moving.X);
        Assert.Same(moving, Assert.Single(_context.Game.Regions["1_0"].Objects).Value);
        Assert.DoesNotContain(moving, _context.Game.Regions["0_0"].Objects.Values);
        Assert.Equal((1, 0, 1), (moving.AttachCount, moving.DetachCount, moving.AddedCount));
    }
}
