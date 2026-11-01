using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HomeBlaze.Game.Tests.Testing;
using Namotion.Interceptor;
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
    public async Task WhenAnObjectIsPlaced_ThenItAppearsInTheObjectsAfterThePublish()
    {
        // Arrange
        // The construction's tick published the empty sets.
        var wall = new Wall { X = 10, Y = 10 };

        // Act
        await _context.RunAsync(_context.Game.PlaceAsync(wall, CancellationToken.None));
        var isPublishedAtOnce = _context.Game.Objects.Count > 0;
        _context.Publish();

        // Assert
        Assert.False(isPublishedAtOnce);
        Assert.Same(wall, Assert.Single(_context.Game.Objects).Value);
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
}
