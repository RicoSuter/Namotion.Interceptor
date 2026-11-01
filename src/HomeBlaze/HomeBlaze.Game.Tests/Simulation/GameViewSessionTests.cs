using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.View;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameViewSessionTests
{
    private readonly GameTestContext _context = new();
    private readonly RecordingViewSink _sink = new();

    private static string GetId(GameViewUpdate update, int x, int y) =>
        Assert.Single(update.Upserted, view => view.X == x && view.Y == y).Id;

    [Fact]
    public async Task WhenASessionJoins_ThenItsFirstUpdateHoldsEverythingInView()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddWall(-20, 8, width: 19);
        _context.AddWall(300, 0);

        // Act
        await _context.OpenViewAsync("tab-1", _sink);

        // Assert
        var (update, panel) = Assert.Single(_sink.Deliveries);
        Assert.Equal(2, update.Upserted.Count);
        var self = Assert.Single(update.Upserted, view => view.IsSelf);
        Assert.Equal((GameViewObject.PlayerKind, 0, 0), (self.Kind, self.X, self.Y));
        var wall = Assert.Single(update.Upserted, view => !view.IsSelf);
        Assert.Equal((-20, 8, 19), (wall.X, wall.Y, wall.Width));
        Assert.Empty(update.Removed);
        Assert.Equal(("X 0 · Y 0 · facing N", true, "tab-1"), (panel.PositionText, panel.HasPlayer, panel.PlayerClientId));
    }

    [Fact]
    public async Task WhenAnObjectInViewChanges_ThenOnlyItIsUpserted()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddWall(-20, 8);
        var door = _context.AddDoor(10, 10);
        await _context.OpenViewAsync("tab-1", _sink);
        var doorId = GetId(_sink.Updates[0], 10, 10);
        _sink.Clear();

        // Act
        door.IsOpen = true;
        _context.Tick();

        // Assert
        var update = Assert.Single(_sink.Updates);
        var view = Assert.Single(update.Upserted);
        Assert.Equal((doorId, false), (view.Id, view.IsMovementBlocking));
        Assert.Empty(update.Removed);
    }

    [Fact]
    public async Task WhenAnObjectOutsideTheViewChanges_ThenNothingIsDelivered()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(300, 0);
        await _context.OpenViewAsync("tab-1", _sink);
        _sink.Clear();

        // Act
        door.IsOpen = true;
        _context.Tick();

        // Assert
        Assert.Empty(_sink.Deliveries);
    }

    [Fact]
    public async Task WhenThePlayerCrossesAChunkBoundary_ThenTheEnteringStripIsUpsertedAndTheLeavingStripRemoved()
    {
        // Arrange: at x 7 the window spans the tiles from -208 to 207, at x 8 those from -192 to 223.
        _context.AddConnectedPlayer("tab-1", x: 7);
        var leaving = _context.AddWall(-205, 0, width: 1);
        var staying = _context.AddWall(-20, 8);
        var entering = _context.AddWall(210, 0, width: 1);
        await _context.OpenViewAsync("tab-1", _sink);
        var first = _sink.Updates[0];
        var leavingId = GetId(first, leaving.X, leaving.Y);
        Assert.Contains(first.Upserted, view => view.X == staying.X);
        Assert.DoesNotContain(first.Upserted, view => view.X == entering.X);
        _sink.Clear();

        // Act
        await _context.StepAsync("tab-1", GameDirection.East);

        // Assert
        var update = Assert.Single(_sink.Updates);
        Assert.Equal(new[] { leavingId }, update.Removed);
        Assert.Equal(2, update.Upserted.Count);
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, X: 8 });
        Assert.Contains(update.Upserted, view => view.X == entering.X);
    }

    [Fact]
    public async Task WhenTheSinkIsBusy_ThenChangesAccumulateAndArriveInOneUpdate()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var first = _context.AddDoor(10, 10);
        var second = _context.AddDoor(-10, 20);
        await _context.OpenViewAsync("tab-1", _sink);
        var firstId = GetId(_sink.Updates[0], 10, 10);
        var secondId = GetId(_sink.Updates[0], -10, 20);
        _sink.Clear();

        // Act
        _sink.IsBusy = true;
        first.IsOpen = true;
        _context.Tick();
        second.IsOpen = true;
        _context.Tick();
        await _context.StepAsync("tab-1", GameDirection.East);
        _context.Publish();
        var deliveredWhileBusy = _sink.Deliveries.Count;
        _sink.IsBusy = false;
        _context.Tick();

        // Assert
        Assert.Equal(0, deliveredWhileBusy);
        var (update, panel) = Assert.Single(_sink.Deliveries);
        Assert.Equal(3, update.Upserted.Count);
        Assert.Contains(update.Upserted, view => view.Id == firstId && !view.IsMovementBlocking);
        Assert.Contains(update.Upserted, view => view.Id == secondId && !view.IsMovementBlocking);
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, X: 1 });
        Assert.Equal("X 1 · Y 0 · facing E", panel.PositionText);
    }

    [Fact]
    public async Task WhenAnObjectIsRemoved_ThenItsIdIsRemoved()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-20, 8);
        await _context.OpenViewAsync("tab-1", _sink);
        var wallId = GetId(_sink.Updates[0], -20, 8);
        _sink.Clear();

        // Act
        await _context.RunAsync(_context.Game.RemoveAsync(wall, CancellationToken.None));

        // Assert
        var update = Assert.Single(_sink.Updates);
        Assert.Equal(new[] { wallId }, update.Removed);
        Assert.Empty(update.Upserted);
    }

    [Fact]
    public async Task WhenAMultiChunkWallChangesInOneChunkOfTheWindow_ThenItStaysKnownOnce()
    {
        // Arrange: the window ends with the chunk of the tiles from 192 to 207, so the wall reaches out of it.
        _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(180, 8, width: 40);
        var door = _context.AddDoor(176, 0, width: 2);
        await _context.OpenViewAsync("tab-1", _sink);
        var wallId = Assert.Single(_sink.Updates[0].Upserted, view => view.X == 180).Id;
        _sink.Clear();

        // Act
        door.IsOpen = true;
        _context.Tick();
        wall.Material = WallMaterial.Wood;
        _context.Tick();
        await _context.RunAsync(_context.Game.RemoveAsync(door, CancellationToken.None));

        // Assert
        var updates = _sink.Updates;
        Assert.Equal(3, updates.Count);
        Assert.DoesNotContain(updates[0].Upserted, view => view.Id == wallId);
        Assert.Equal(wallId, Assert.Single(updates[1].Upserted).Id);
        Assert.Empty(updates[2].Upserted);
        Assert.All(updates, update => Assert.DoesNotContain(wallId, update.Removed));
    }

    [Fact]
    public async Task WhenThePlayerTurns_ThenTheTargetsInThePanelFollow()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var north = _context.AddWall(-1, 2);
        var east = _context.AddWall(2, 0, width: 1, depth: 2);
        await _context.OpenViewAsync("tab-1", _sink);
        var before = _sink.Panel.Targets;

        // Act
        await _context.StepAsync("tab-1", GameDirection.East, turnOnly: true);

        // Assert
        Assert.Same(north, Assert.Single(before));
        Assert.Same(east, Assert.Single(_sink.Panel.Targets));
        Assert.Equal(GameDirection.East, _sink.Panel.Facing);
    }

    [Fact]
    public async Task WhenAnObjectIsPlacedInFront_ThenTheTargetsInThePanelFollow()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        await _context.OpenViewAsync("tab-1", _sink);

        // Act
        var wall = _context.AddWall(-1, 2);

        // Assert
        Assert.Same(wall, Assert.Single(_sink.Panel.Targets));
    }

    [Fact]
    public async Task WhenThePlayerOfTheSessionAppears_ThenTheSessionFollowsIt()
    {
        // Arrange
        _context.AddWall(-20, 8);
        await _context.OpenViewAsync("tab-1", _sink);
        var (first, firstPanel) = Assert.Single(_sink.Deliveries);
        _sink.Clear();

        // Act
        _context.AddConnectedPlayer("tab-1", x: 1000);

        // Assert
        Assert.Equal((GamePanelState.NoPosition, false), (firstPanel.PositionText, firstPanel.HasPlayer));
        var wallId = Assert.Single(first.Upserted).Id;
        var updates = _sink.Updates;
        Assert.Contains(updates, update => update.Removed.Contains(wallId));
        Assert.Contains(updates, update => update.Upserted.Any(view => view is { IsSelf: true, X: 1000 }));
        Assert.Equal(("X 1000 · Y 0 · facing N", "tab-1"), (_sink.Panel.PositionText, _sink.Panel.PlayerClientId));
    }

    [Fact]
    public async Task WhenAPlayerInViewDisconnects_ThenItIsRemoved()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        await _context.OpenViewAsync("tab-1", _sink);
        var otherId = Assert.Single(_sink.Updates[0].Upserted, view => !view.IsSelf).Id;
        _sink.Clear();

        // Act
        _context.Clients.Set("tab-2", isNamed: false, isConnected: false);
        _context.Reconcile();

        // Assert
        var update = Assert.Single(_sink.Updates);
        Assert.Equal(new[] { otherId }, update.Removed);
        Assert.Empty(update.Upserted);
    }

    [Fact]
    public async Task WhenTheSessionIsDisposed_ThenNothingMoreIsDelivered()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(10, 10);
        var session = await _context.OpenViewAsync("tab-1", _sink);
        _sink.Clear();

        // Act
        session.Dispose();
        door.IsOpen = true;
        _context.Tick();

        // Assert
        Assert.Empty(_sink.Deliveries);
    }

    [Fact]
    public async Task WhenTheSubjectsArePublished_ThenThePanelIsDeliveredAgain()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 300);
        await _context.OpenViewAsync("tab-1", _sink);
        _sink.Clear();

        // Act
        await _context.StepAsync("tab-2", GameDirection.East);
        _context.Publish();

        // Assert
        var (update, _) = Assert.Single(_sink.Deliveries);
        Assert.True(update.IsEmpty);
    }
}
