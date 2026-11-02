using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.View;

namespace HomeBlaze.Game.Tests.Simulation;

public class GameViewSessionTests
{
    private readonly GameTestContext _context = new();
    private readonly RecordingViewSink _sink = new();

    private static string GetId(GameViewUpdate update, int x, int y) =>
        Assert.Single(update.Upserted, view => view.Payload.X == x && view.Payload.Y == y).Id;

    [Fact]
    public void WhenASessionJoins_ThenItsFirstUpdateHoldsEverythingInView()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddWall(-20, 8, width: 19);
        _context.AddWall(300, 0);

        // Act
        _context.OpenView("tab-1", _sink);

        // Assert
        var (update, panel) = Assert.Single(_sink.Deliveries);
        Assert.Equal(2, update.Upserted.Count);
        var self = Assert.Single(update.Upserted, view => view.IsSelf);
        Assert.Equal((GameViewPayload.PlayerKind, 0, 0), (self.Payload.Kind, self.Payload.X, self.Payload.Y));
        var wall = Assert.Single(update.Upserted, view => !view.IsSelf);
        Assert.Equal((-20, 8, 19), (wall.Payload.X, wall.Payload.Y, wall.Payload.Width));
        Assert.Empty(update.Removed);
        Assert.Equal(("X 0 · Y 0 · facing N", true, "tab-1"), (panel.PositionText, panel.HasPlayer, panel.PlayerClientId));
    }

    [Fact]
    public void WhenAnObjectInViewChanges_ThenOnlyItIsUpserted()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddWall(-20, 8);
        var door = _context.AddDoor(10, 10);
        _context.OpenView("tab-1", _sink);
        var doorId = GetId(_sink.Updates[0], 10, 10);
        _sink.Clear();

        // Act
        door.IsOpen = true;
        _context.Tick();

        // Assert
        var update = Assert.Single(_sink.Updates);
        var view = Assert.Single(update.Upserted);
        Assert.Equal((doorId, false), (view.Id, view.Payload.IsMovementBlocking));
        Assert.Empty(update.Removed);
    }

    [Fact]
    public void WhenAnObjectOutsideTheViewChanges_ThenNothingIsDelivered()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(300, 0);
        _context.OpenView("tab-1", _sink);
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
        _context.OpenView("tab-1", _sink);
        var first = _sink.Updates[0];
        var leavingId = GetId(first, leaving.X, leaving.Y);
        Assert.Contains(first.Upserted, view => view.Payload.X == staying.X);
        Assert.DoesNotContain(first.Upserted, view => view.Payload.X == entering.X);
        _sink.Clear();

        // Act
        await _context.StepAsync("tab-1", GameDirection.East);

        // Assert
        var update = Assert.Single(_sink.Updates);
        Assert.Equal(new[] { leavingId }, update.Removed);
        Assert.Equal(2, update.Upserted.Count);
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, Payload.X: 8 });
        Assert.Contains(update.Upserted, view => view.Payload.X == entering.X);
    }

    [Fact]
    public async Task WhenTheSinkIsBusy_ThenChangesAccumulateAndArriveInOneUpdate()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var first = _context.AddDoor(10, 10);
        var second = _context.AddDoor(-10, 20);
        _context.OpenView("tab-1", _sink);
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
        Assert.Contains(update.Upserted, view => view.Id == firstId && !view.Payload.IsMovementBlocking);
        Assert.Contains(update.Upserted, view => view.Id == secondId && !view.Payload.IsMovementBlocking);
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, Payload.X: 1 });
        Assert.Equal("X 1 · Y 0 · facing E", panel.PositionText);
    }

    [Fact]
    public async Task WhenAnObjectIsRemoved_ThenItsIdIsRemoved()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var wall = _context.AddWall(-20, 8);
        _context.OpenView("tab-1", _sink);
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
        _context.OpenView("tab-1", _sink);
        var wallId = Assert.Single(_sink.Updates[0].Upserted, view => view.Payload.X == 180).Id;
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
        _context.OpenView("tab-1", _sink);
        var before = _sink.Panel.Targets;

        // Act
        await _context.StepAsync("tab-1", GameDirection.East, turnOnly: true);

        // Assert
        Assert.Same(north, Assert.Single(before));
        Assert.Same(east, Assert.Single(_sink.Panel.Targets));
        Assert.Equal(GameDirection.East, _sink.Panel.Facing);
    }

    [Fact]
    public void WhenAnObjectIsPlacedInFront_ThenTheTargetsInThePanelFollow()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.OpenView("tab-1", _sink);

        // Act
        var wall = _context.AddWall(-1, 2);

        // Assert
        Assert.Same(wall, Assert.Single(_sink.Panel.Targets));
    }

    [Fact]
    public void WhenThePlayerOfTheSessionAppears_ThenTheSessionFollowsIt()
    {
        // Arrange
        _context.AddWall(-20, 8);
        _context.OpenView("tab-1", _sink);
        var (first, firstPanel) = Assert.Single(_sink.Deliveries);
        _sink.Clear();

        // Act
        _context.AddConnectedPlayer("tab-1", x: 1000);

        // Assert
        Assert.Equal((GamePanelState.NoPosition, false), (firstPanel.PositionText, firstPanel.HasPlayer));
        var wallId = Assert.Single(first.Upserted).Id;
        var updates = _sink.Updates;
        Assert.Contains(updates, update => update.Removed.Contains(wallId));
        Assert.Contains(updates, update => update.Upserted.Any(view => view is { IsSelf: true, Payload.X: 1000 }));
        Assert.Equal(("X 1000 · Y 0 · facing N", "tab-1"), (_sink.Panel.PositionText, _sink.Panel.PlayerClientId));
    }

    [Fact]
    public void WhenAPlayerInViewDisconnects_ThenItIsRemoved()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        _context.OpenView("tab-1", _sink);
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
    public void WhenTheSessionIsDisposed_ThenNothingMoreIsDelivered()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var door = _context.AddDoor(10, 10);
        var session = _context.OpenView("tab-1", _sink);
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
        _context.OpenView("tab-1", _sink);
        _sink.Clear();

        // Act
        await _context.StepAsync("tab-2", GameDirection.East);
        _context.Publish();

        // Assert
        var (update, _) = Assert.Single(_sink.Deliveries);
        Assert.True(update.IsEmpty);
    }

    [Fact]
    public async Task WhenTheSinkIsBusyWhileThePlayerCrossesAChunkBoundary_ThenTheStripsArriveInOneUpdate()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1", x: 7);
        var leaving = _context.AddWall(-205, 0, width: 1);
        var entering = _context.AddWall(210, 0, width: 1);
        _context.OpenView("tab-1", _sink);
        var leavingId = GetId(_sink.Updates[0], leaving.X, leaving.Y);
        _sink.Clear();

        // Act
        _sink.IsBusy = true;
        await _context.StepAsync("tab-1", GameDirection.East);
        _context.Tick();
        _sink.IsBusy = false;
        _context.Tick();

        // Assert
        var update = Assert.Single(_sink.Updates);
        Assert.Equal(new[] { leavingId }, update.Removed);
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, Payload.X: 8 });
        Assert.Contains(update.Upserted, view => view.Payload.X == entering.X);
    }

    [Fact]
    public async Task WhenAnEntityMovesFromTheLeavingStripIntoTheEnteringStrip_ThenItIsUpsertedAndNotRemoved()
    {
        // Arrange: at x 7 the window spans the tiles from -208 to 207, at x 8 those from -192 to 223.
        _context.AddConnectedPlayer("tab-1", x: 7);
        var other = _context.AddConnectedPlayer("tab-2", x: -205);
        _context.OpenView("tab-1", _sink);
        var otherId = GetId(_sink.Updates[0], -205, 0);
        _sink.Clear();

        // Act: the teleport and the step run in the same tick.
        other.X = 210;
        await _context.StepAsync("tab-1", GameDirection.East);

        // Assert
        var update = Assert.Single(_sink.Updates);
        Assert.Empty(update.Removed);
        Assert.Contains(update.Upserted, view => view.Id == otherId && view.Payload.X == 210);
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, Payload.X: 8 });
    }

    [Fact]
    public void WhenTheSessionSwitchesToAnotherPlayer_ThenItFollowsThatPlayer()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 1000);
        var session = _context.OpenView("tab-1", _sink);
        var formerId = Assert.Single(_sink.Updates[0].Upserted, view => view.IsSelf).Id;
        _sink.Clear();

        // Act
        session.ClientId = "tab-2";
        _context.Tick();

        // Assert
        var (update, panel) = Assert.Single(_sink.Deliveries);
        Assert.Equal(new[] { formerId }, update.Removed);
        Assert.Contains(update.Upserted, view => view is { IsSelf: true, Payload.X: 1000 });
        Assert.Equal(("tab-2", "X 1000 · Y 0 · facing N"), (panel.PlayerClientId, panel.PositionText));
    }

    [Fact]
    public void WhenManySessionsNeedAFullRefresh_ThenTheyAreServedOverSeveralTicks()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var sinks = new RecordingViewSink[GameViewSessions.MaximumFullRefreshesPerTick + 2];
        for (var i = 0; i < sinks.Length; i++)
        {
            sinks[i] = new RecordingViewSink();
            _context.Game.Views.Open("tab-1", sinks[i]);
        }

        // Act
        _context.Tick();
        var servedInFirstTick = sinks.Count(sink => sink.Deliveries.Count > 0);
        _context.Tick();
        var servedInSecondTick = sinks.Count(sink => sink.Deliveries.Count > 0);

        // Assert
        Assert.Equal(GameViewSessions.MaximumFullRefreshesPerTick, servedInFirstTick);
        Assert.Equal(sinks.Length, servedInSecondTick);
        Assert.All(sinks, sink => Assert.Contains(Assert.Single(sink.Deliveries).Update.Upserted, view => view.IsSelf));
    }

    [Fact]
    public void WhenNothingChanges_ThenATickWithOpenSessionsAllocatesNothing()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        _context.AddWall(-20, 8);
        _context.OpenView("tab-1", _sink);
        _context.OpenView("tab-2", new RecordingViewSink());
        _context.Publish();
        _context.Tick(5);
        _sink.Clear();

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        _context.Tick();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Empty(_sink.Deliveries);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenADeferredFullRefreshLosesItsReason_ThenTheChangesOfTheDeferredTickStillArrive()
    {
        // Arrange: the sessions opened later, which follow a player that stays, take the full refreshes of the tick
        // before the first session's turn.
        var player = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 20);
        // A chunk apart from the player's, so the jump back does not refresh it.
        var door = _context.AddDoor(100, 100);
        _context.OpenView("tab-1", _sink);
        var doorId = GetId(_sink.Updates[0], 100, 100);
        _sink.Clear();
        for (var i = 0; i < GameViewSessions.MaximumFullRefreshesPerTick; i++)
        {
            _context.Game.Views.Open("tab-2", new RecordingViewSink());
        }

        // Act
        player.X = 5000;
        door.IsOpen = true;
        _context.Tick();
        var deliveredInDeferredTick = _sink.Deliveries.Count;
        player.X = 0;
        _context.Tick();

        // Assert
        Assert.Equal(0, deliveredInDeferredTick);
        Assert.Contains(_sink.Updates, update => update.Upserted.Any(view => view.Id == doorId && !view.Payload.IsMovementBlocking));
    }

    [Fact]
    public void WhenTheSinkIsBusyWhileEntitiesMoveInAndOutOfTheWindow_ThenTheNextUpdateCarriesThemAll()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        var other = _context.AddConnectedPlayer("tab-2", x: 10);
        var leaving = _context.AddWall(-20, 8);
        var entering = _context.AddWall(1000, 0);
        var door = _context.AddDoor(10, 10);
        _context.OpenView("tab-1", _sink);
        var first = _sink.Updates[0];
        var otherId = GetId(first, 10, 0);
        var leavingId = GetId(first, -20, 8);
        var doorId = GetId(first, 10, 10);
        _sink.Clear();

        // Act
        _sink.IsBusy = true;
        other.X = 20;
        door.IsOpen = true;
        _context.Tick();
        other.X = 30;
        leaving.X = 1000;
        leaving.Y = 100;
        entering.X = 40;
        _context.Tick();
        var deliveredWhileBusy = _sink.Deliveries.Count;
        _sink.IsBusy = false;
        _context.Tick();

        // Assert
        Assert.Equal(0, deliveredWhileBusy);
        var update = Assert.Single(_sink.Updates);
        Assert.Equal(new[] { leavingId }, update.Removed);
        Assert.Equal(3, update.Upserted.Count);
        Assert.Contains(update.Upserted, view => view.Id == otherId && view.Payload.X == 30);
        Assert.Contains(update.Upserted, view => view.Id == doorId && !view.Payload.IsMovementBlocking);
        Assert.Contains(update.Upserted, view => view is { IsSelf: false, Payload.X: 40, Payload.Y: 0 });
    }

    [Fact]
    public void WhenABusySinkMissesMoreChangedEntitiesThanTheBound_ThenTheNextUpdateRefreshesTheWholeWindow()
    {
        // Arrange
        _context.AddConnectedPlayer("tab-1");
        _context.OpenView("tab-1", _sink);
        _sink.Clear();
        var objects = new Dictionary<string, GameObject>();
        for (var i = 0; i <= GameViewSession.MaximumPendingEntities; i++)
        {
            objects[$"box-{i}"] = new VisualObject { X = 10 + i % 64 * 2, Y = 10 + i / 64 * 2 };
        }

        // Act
        _sink.IsBusy = true;
        _context.Game.SetObjects(objects);
        _context.Tick();
        var moved = (VisualObject)objects["box-0"];
        moved.X = 9;
        _context.Tick();

        // Sessions opened later are updated first in a tick and take its full refreshes, so the busy session's full
        // refresh waits a tick.
        for (var i = 0; i < GameViewSessions.MaximumFullRefreshesPerTick; i++)
        {
            _context.Game.Views.Open("tab-1", new RecordingViewSink());
        }

        _sink.IsBusy = false;
        _context.Tick();
        var deliveredWhileDeferred = _sink.Deliveries.Count;
        _context.Tick();

        // Assert
        Assert.Equal(0, deliveredWhileDeferred);
        var update = Assert.Single(_sink.Updates);
        Assert.Equal(objects.Count, update.Upserted.Count);
        Assert.Contains(update.Upserted, view => view.Payload is { X: 9, Y: 10 });
        Assert.Empty(update.Removed);
    }
}

