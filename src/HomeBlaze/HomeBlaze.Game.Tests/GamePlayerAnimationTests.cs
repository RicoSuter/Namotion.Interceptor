using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Tests.Testing;

namespace HomeBlaze.Game.Tests;

public class GamePlayerAnimationTests
{
    private readonly GameTestContext _context = new();

    private static int TicksOfLimit => (int)(GameSimulationSettings.AnimationLimit / GameSimulationSettings.TickInterval) + 1;

    [Fact]
    public void WhenAPlayerPlaysAnAnimation_ThenItsSubjectShowsItAfterThePublish()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");

        // Act
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Clapping", GameVisualAnimationMode.Once);
        _context.Publish();

        // Assert
        Assert.Equal("Clapping", player.Animation);
    }

    [Fact]
    public void WhenTheSameAnimationPlaysTwice_ThenTheViewSeesTwoSequences()
    {
        // Arrange
        var sink = new RecordingViewSink();
        _context.AddConnectedPlayer("tab-1");
        using var view = _context.OpenView("tab-1", sink);
        var caller = new GameCaller("tab-1");

        // Act
        _context.Game.PlayAnimation(caller, "Clapping", GameVisualAnimationMode.Once);
        _context.Tick();
        var first = sink.LastSelf().Payload;
        _context.Game.PlayAnimation(caller, "Clapping", GameVisualAnimationMode.Once);
        _context.Tick();
        var second = sink.LastSelf().Payload;

        // Assert
        Assert.Equal("Clapping", first.Animation);
        Assert.Equal((int)GameVisualAnimationMode.Once, first.AnimationMode);
        Assert.Equal("Clapping", second.Animation);
        Assert.NotEqual(first.AnimationSequence, second.AnimationSequence);
    }

    [Fact]
    public void WhenAnAnimationChangesItsMode_ThenTheViewSeesTheNewMode()
    {
        // Arrange
        var sink = new RecordingViewSink();
        _context.AddConnectedPlayer("tab-1");
        using var view = _context.OpenView("tab-1", sink);
        var caller = new GameCaller("tab-1");
        _context.Game.PlayAnimation(caller, "Sitting", GameVisualAnimationMode.Once);
        _context.Tick();

        // Act
        _context.Game.PlayAnimation(caller, "Sitting", GameVisualAnimationMode.Loop);
        _context.Tick();

        // Assert
        Assert.Equal((int)GameVisualAnimationMode.Loop, sink.LastSelf().Payload.AnimationMode);
    }

    [Fact]
    public void WhenAnAnimationIsCleared_ThenTheViewSeesNoAnimation()
    {
        // Arrange
        var sink = new RecordingViewSink();
        _context.AddConnectedPlayer("tab-1");
        using var view = _context.OpenView("tab-1", sink);
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Jump", GameVisualAnimationMode.Once);
        _context.Tick();

        // Act
        _context.Tick(TicksOfLimit);

        // Assert
        Assert.Null(sink.LastSelf().Payload.Animation);
    }

    [Fact]
    public void WhenAOnceAnimationRunsLongerThanTheLimit_ThenItIsCleared()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Jump", GameVisualAnimationMode.Once);

        // Act
        _context.Tick(TicksOfLimit);
        _context.Publish();

        // Assert
        Assert.Null(player.Animation);
    }

    [Fact]
    public async Task WhenAPlayerStepsDuringALoopAnimation_ThenItIsCleared()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Sitting", GameVisualAnimationMode.Loop);
        _context.Tick(TicksOfLimit);

        // Act
        await _context.StepAsync("tab-1", GameDirection.East);
        _context.Publish();

        // Assert
        Assert.Null(player.Animation);
    }

    [Fact]
    public async Task WhenAPlayerOnlyTurnsDuringALoopAnimation_ThenItStays()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Sitting", GameVisualAnimationMode.Loop);
        _context.Tick();

        // Act
        await _context.StepAsync("tab-1", GameDirection.East, turnOnly: true);
        _context.Publish();

        // Assert
        Assert.Equal("Sitting", player.Animation);
    }

    [Fact]
    public void WhenALoopAnimationRunsLongerThanTheLimit_ThenItStays()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Sitting", GameVisualAnimationMode.Loop);

        // Act
        _context.Tick(TicksOfLimit);
        _context.Publish();

        // Assert
        Assert.Equal("Sitting", player.Animation);
    }

    [Fact]
    public void WhenThereIsNoPlayerForTheCaller_ThenPlayingThrows()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _context.Game.PlayAnimation(null, "Clapping", GameVisualAnimationMode.Once));
        Assert.Throws<InvalidOperationException>(() => _context.Game.PlayAnimation(new GameCaller("nobody"), "Clapping", GameVisualAnimationMode.Once));
    }

    [Theory]
    [InlineData("Idle")]
    [InlineData("Walk")]
    [InlineData("")]
    public void WhenAReservedOrEmptyAnimationIsPlayed_ThenPlayingThrows(string animation)
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _context.Game.PlayAnimation(new GameCaller("tab-1"), animation, GameVisualAnimationMode.Once));
    }

    [Fact]
    public void WhenThePlayerDisconnects_ThenItsAnimationIsCleared()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1", isNamed: true);
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Sitting", GameVisualAnimationMode.Loop);
        _context.Publish();

        // Act
        _context.Clients.Set("tab-1", isNamed: true, isConnected: false);
        _context.Reconcile();
        _context.Publish();

        // Assert
        Assert.Null(player.Animation);
    }

    [Fact]
    public void WhenAnAnonymousPlayerLeaves_ThenItsAnimationIsCleared()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.PlayAnimation(new GameCaller("tab-1"), "Sitting", GameVisualAnimationMode.Loop);
        _context.Publish();

        // Act
        _context.Clients.Remove("tab-1");
        _context.Reconcile();
        _context.Publish();

        // Assert
        Assert.Null(player.Animation);
    }
}
