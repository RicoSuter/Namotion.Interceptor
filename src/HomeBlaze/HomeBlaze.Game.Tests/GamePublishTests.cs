using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HomeBlaze.Game.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Game.Tests;

public class GamePublishTests
{
    private readonly GameTestContext _context = new();

    // Ticks that stay below the publish interval, counted from a tick that published.
    private static readonly int TicksWithinInterval = (int)(GameSimulationSettings.PublishInterval / GameSimulationSettings.TickInterval) - 1;

    private IObservable<Namotion.Interceptor.Tracking.Change.SubjectPropertyChange> Changes =>
        ((IInterceptorSubject)_context.Root).Context.GetPropertyChangeObservable(ImmediateScheduler.Instance);

    [Fact]
    public async Task WhenAPlayerSteps_ThenTheSubjectShowsItOnlyAfterThePublishInterval()
    {
        // Arrange
        // Its last tick published the position it was moved to.
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        var step = _context.Game.StepAsync("tab-1", GameDirection.East, turnOnly: false);
        _context.Tick();
        var result = await step;
        var xWithinInterval = player.X;
        _context.Tick(TicksWithinInterval);
        var xAfterInterval = player.X;

        // Assert
        Assert.Equal(1, result!.Value.X);
        Assert.Equal(0, xWithinInterval);
        Assert.Equal(1, xAfterInterval);
    }

    [Fact]
    public async Task WhenAPlayerStepsTwiceWithinTheInterval_ThenOnePublishWritesTheLatestPosition()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var published = new List<int>();
        using var subscription = Changes
            .Where(change => ReferenceEquals(change.Property.Subject, player) && change.Property.Name == nameof(GamePlayer.X))
            .Subscribe(change => published.Add(change.GetNewValue<int>()));

        // Act
        var first = _context.Game.StepAsync("tab-1", GameDirection.East, turnOnly: false);
        _context.Tick();
        var second = _context.Game.StepAsync("tab-1", GameDirection.East, turnOnly: false);
        _context.Tick();
        await Task.WhenAll(first, second);
        _context.Publish();

        // Assert
        Assert.Equal(new[] { 2 }, published);
        Assert.Equal(2, player.X);
    }

    [Fact]
    public async Task WhenAnObjectIsPlaced_ThenItIsInTheObjectsOnceThePlaceCompletes()
    {
        // Arrange
        // The construction's tick published the empty sets, so the publish interval has not passed yet.
        var wall = new Wall { X = 10, Y = 10 };

        // Act
        await _context.RunAsync(_context.Game.PlaceAsync(wall, CancellationToken.None));

        // Assert
        Assert.Same(wall, Assert.Single(_context.Game.Objects).Value);
    }

    [Fact]
    public async Task WhenAnObjectIsPlacedWhilePublishingIsPaused_ThenThePlaceCompletesOnceTheSaveCompletedAndTheObjectIsPublished()
    {
        // Arrange
        _context.AddWall(0, 0);
        var blocker = _context.HoldSave();
        var wall = new Wall { X = 10, Y = 10 };

        // Act
        var place = _context.Game.PlaceAsync(wall, CancellationToken.None);
        _context.Game.RunTickForTest();
        _context.Game.RunTickForTest();
        var isCompletedDuringSave = place.IsCompleted;
        var objectCountDuringSave = _context.Game.Objects.Count;
        await _context.ReleaseSaveAsync(blocker);

        // Assert
        await place;
        Assert.False(isCompletedDuringSave);
        Assert.Equal(1, objectCountDuringSave);
        Assert.Same(wall, _context.Game.Objects.Values.Last());
    }

    [Fact]
    public async Task WhenTheWriteOfTheObjectsFails_ThenTheNextTickWritesThemAgain()
    {
        // Arrange
        var failingInterceptor = new FailingWriteInterceptor(nameof(Game.Objects));
        var context = new GameTestContext(subjectContext: InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithParents()
            .WithService<IWriteInterceptor>(() => failingInterceptor, existing => ReferenceEquals(existing, failingInterceptor)));
        var wall = new Wall { X = 10, Y = 10 };
        failingInterceptor.IsArmed = true;

        // Act
        await context.RunAsync(context.Game.PlaceAsync(wall, CancellationToken.None));

        // Assert
        Assert.Equal(1, failingInterceptor.FailureCount);
        Assert.Same(wall, Assert.Single(context.Game.Objects).Value);
    }

    [Fact]
    public void WhenAPlayerJoins_ThenItsSubjectKnowsItsClientBeforeItIsPublished()
    {
        // Arrange
        var clientIds = new List<(string Key, string ClientId)>();
        using var subscription = Changes
            .Where(change => ReferenceEquals(change.Property.Subject, _context.Game) && change.Property.Name == nameof(Game.Players))
            .Subscribe(change =>
            {
                foreach (var (key, player) in change.GetNewValue<Dictionary<string, GamePlayer>>())
                {
                    clientIds.Add((key, player.ClientId));
                }
            });
        _context.Clients.Set("tab-1", isNamed: false, isConnected: true);

        // Act
        _context.Reconcile();

        // Assert
        Assert.Equal(new[] { ("tab-1", "tab-1") }, clientIds);
    }

    [Fact]
    public async Task WhenAPlacedObjectIsWrittenBeforeItIsPublished_ThenTheGameReadsTheWriteAfterThePublish()
    {
        // Arrange
        // Without the app's registry, which writes properties of an object when the publish attaches it.
        var context = new GameTestContext(subjectContext: InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents());
        context.AddWall(0, 0);
        var blocker = context.HoldSave();
        var wall = new Wall { X = 10, Y = 10 };

        // The held save pauses publishing, so the placed wall is not published at once.
        var place = context.Game.PlaceAsync(wall, CancellationToken.None);
        context.Game.RunTickForTest();
        var isPublishedAtOnce = context.Game.Objects.ContainsValue(wall);

        // Act
        // Unseen by the write interceptor, because the wall is not attached to the game yet.
        wall.X = 30;
        await context.ReleaseSaveAsync(blocker);
        await place;

        // Assert
        Assert.False(isPublishedAtOnce);
        Assert.True(context.Game.IsFree(new GameArea(10, 10, 4, 1)));
        Assert.False(context.Game.IsFree(wall.Area));
    }

    [Fact]
    public async Task WhenAnObserverWritesTheObjectsDuringAPublish_ThenTheWriteIsTakenOver()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var extra = new Wall { X = 30, Y = 30 };
        var isWritten = false;
        using var subscription = Changes
            .Where(change => ReferenceEquals(change.Property.Subject, player) && change.Property.Name == nameof(GamePlayer.X))
            .Subscribe(_ =>
            {
                if (!isWritten)
                {
                    isWritten = true;
                    _context.Game.Objects = new Dictionary<string, GameObject>(_context.Game.Objects) { ["extra"] = extra };
                }
            });

        // Act
        await _context.StepAsync("tab-1", GameDirection.East);
        _context.Publish();

        // Assert
        Assert.True(isWritten);
        Assert.Same(extra, _context.Game.Objects["extra"]);
        Assert.False(_context.Game.IsFree(extra.Area));
    }

    [Fact]
    public async Task WhenAnObserverWritesThePositionOfAPlayerDuringAPublish_ThenThePlayerIsMovedThere()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var isWritten = false;
        using var subscription = Changes
            .Where(change => ReferenceEquals(change.Property.Subject, player) && change.Property.Name == nameof(GamePlayer.Facing))
            .Subscribe(_ =>
            {
                if (!isWritten)
                {
                    isWritten = true;
                    player.X = 50;
                }
            });

        // Act
        await _context.StepAsync("tab-1", GameDirection.West, turnOnly: true);
        _context.Publish();
        var result = await _context.StepAsync("tab-1", GameDirection.East);
        _context.Publish();

        // Assert
        Assert.True(isWritten);
        Assert.Equal(51, result!.Value.X);
        Assert.Equal(51, player.X);
    }
}
