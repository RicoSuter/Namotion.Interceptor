using BenchmarkDotNet.Attributes;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.View;
using HomeBlaze.Game.Voice;
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Game.Benchmark;

/// <summary>
/// The tick of a game with 200 players and 100k objects, and its parts one by one. The parts run on a stopped
/// simulation, so no tick runs around them. The clustered rows put all players within view of each other.
/// </summary>
/// <remarks>
/// A tick only starts a volume table build on the thread pool, when one is due and none runs, and never waits for it;
/// the VolumeTableBuild rows measure a build on its own. Their players step east and west in turns, so from the second
/// build on each source of a path search was searched before, unless a door changed the sound blocked tiles meanwhile.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 5, iterationCount: 20)]
public class SimulationTickBenchmark
{
    private const int PlayerCount = 200;
    private const int ObjectCount = 100_000;
    private const int MovingPlayersInView = 10;

    private readonly GameVolumeTableBuilder _volumeTableBuilder = new();

    private BenchmarkWorld _world = null!;
    private Action _publishMovedPlayers = null!;
    private Action _placeOrRemoveAndPublishObjectSet = null!;
    private GameViewSession _session = null!;
    private OccupancySnapshot _snapshotBeforeMoves = null!;
    private SoundSnapshot _soundSnapshot = null!;
    private GameEntity _door = null!;
    private GameArea _placeArea;
    private Tree? _placedTree;
    private int _invocation;

    [GlobalSetup(Target = nameof(TickWithAllPlayersWalking))]
    public void SetupGroups() => _world = BenchmarkWorld.Create(PlayerCount, ObjectCount);

    [GlobalSetup(Target = nameof(TickWithAllPlayersWalkingClustered))]
    public void SetupClustered() => _world = BenchmarkWorld.Create(PlayerCount, ObjectCount, PlayerSpread.Clustered);

    [GlobalSetup(Targets = new[]
    {
        nameof(PublishMovedPlayers), nameof(MovePlayers), nameof(MovePlayersAndPublishSnapshot), nameof(VolumeTableBuildWithAllPlayersMoved),
        nameof(VolumeTableBuildAfterADoorToggled), nameof(ViewSessionsWithAllPlayersMoved)
    })]
    public void SetupStopped() => SetupStopped(PlayerSpread.Groups);

    [GlobalSetup(Targets = new[]
    {
        nameof(VolumeTableBuildWithAllPlayersMovedClustered), nameof(VolumeTableBuildAfterADoorToggledClustered),
        nameof(ViewSessionsWithAllPlayersMovedClustered)
    })]
    public void SetupStoppedClustered() => SetupStopped(PlayerSpread.Clustered);

    [GlobalSetup(Target = nameof(ViewSessionWithTenMovingPlayers))]
    public void SetupViewSession()
    {
        SetupStopped();

        // A session of its own, outside the game's sessions, which no tick updates anymore.
        _session = new GameViewSession(_world.ClientIds[0], DiscardingSink.Instance);
        _session.Update(_world.World, _world.Game.PublishVersion, canFullRefresh: true, NullLogger.Instance);
        _world.World.Index.Changes.Clear();
    }

    [GlobalSetup(Target = nameof(PublishObjectSetAfterOnePlace))]
    public void SetupPlace()
    {
        SetupStopped();
        _placeArea = _world.FindFreeArea(BenchmarkLayout.CopyWidth * 2, BenchmarkLayout.CopyDepth * 2, width: 3, depth: 3);
        _placeOrRemoveAndPublishObjectSet = PlaceOrRemoveAndPublishObjectSet;
    }

    [GlobalSetup(Target = nameof(OccupancySnapshotWithMovedPlayers))]
    public void SetupSnapshot()
    {
        SetupStopped();
        _snapshotBeforeMoves = _world.World.Snapshot;
        MovePlayersInIndex(_world.Players, offset: 1);
    }

    /// <summary>
    /// One tick in which every player takes a step, with all view sessions updating.
    /// </summary>
    [Benchmark]
    public void TickWithAllPlayersWalking() => _world.TickWithAllPlayersStepping();

    /// <summary>
    /// One tick in which every player takes a step, with all players and their view sessions in one area, so every
    /// session sees every step.
    /// </summary>
    [Benchmark]
    public void TickWithAllPlayersWalkingClustered() => _world.TickWithAllPlayersStepping();

    /// <summary>
    /// One publish of 200 moved players to their subjects.
    /// </summary>
    [Benchmark]
    public void PublishMovedPlayers() => _world.RunOnSimulation(_publishMovedPlayers);

    /// <summary>
    /// One view session update with 10 other players moving in view, including moving them in the index.
    /// </summary>
    [Benchmark]
    public object? ViewSessionWithTenMovingPlayers()
    {
        MovePlayersInIndex(_world.Players.AsSpan(1, MovingPlayersInView), NextOffset());
        _session.Update(_world.World, _world.Game.PublishVersion, canFullRefresh: true, NullLogger.Instance);
        _world.World.Index.Changes.Clear();
        return DiscardingSink.Instance.LastUpdate;
    }

    /// <summary>
    /// The update of all 200 view sessions after every player moved, including moving them in the index.
    /// </summary>
    [Benchmark]
    public void ViewSessionsWithAllPlayersMoved() => UpdateViewSessionsAfterMoves();

    /// <summary>
    /// The update of all 200 view sessions after every player moved, all players within view of each other.
    /// </summary>
    [Benchmark]
    public void ViewSessionsWithAllPlayersMovedClustered() => UpdateViewSessionsAfterMoves();

    /// <summary>
    /// A volume table build after every player moved, as the thread pool runs it, including moving the players in the
    /// index and copying their positions as the tick does.
    /// </summary>
    [Benchmark]
    public object VolumeTableBuildWithAllPlayersMoved() => BuildVolumeTableAfterMoves(isDoorToggled: false);

    /// <summary>
    /// A volume table build after every player moved, all players within view of each other.
    /// </summary>
    [Benchmark]
    public object VolumeTableBuildWithAllPlayersMovedClustered() => BuildVolumeTableAfterMoves(isDoorToggled: false);

    /// <summary>
    /// A volume table build after every player moved and a door opened or closed, which drops the regions and paths
    /// the builds keep, including the sound snapshot of the door.
    /// </summary>
    [Benchmark]
    public object VolumeTableBuildAfterADoorToggled() => BuildVolumeTableAfterMoves(isDoorToggled: true);

    /// <summary>
    /// A volume table build after every player moved and a door opened or closed, all players within view of each other.
    /// </summary>
    [Benchmark]
    public object VolumeTableBuildAfterADoorToggledClustered() => BuildVolumeTableAfterMoves(isDoorToggled: true);

    /// <summary>
    /// Places one object into the 100k, or removes the one placed before, and publishes the object set, which writes a
    /// new dictionary of the objects of its region to <see cref="GameRegion.Objects"/>.
    /// </summary>
    [Benchmark]
    public void PublishObjectSetAfterOnePlace() => _world.RunOnSimulation(_placeOrRemoveAndPublishObjectSet);

    /// <summary>
    /// The occupancy snapshot of a tick in which 200 players moved, from the snapshot before.
    /// </summary>
    [Benchmark]
    public object OccupancySnapshotWithMovedPlayers() =>
        _snapshotBeforeMoves.With(_world.World.Index, _world.World.Index.Changes);

    /// <summary>
    /// Moves 200 players as outside writes of their position do; the baseline of <see cref="MovePlayersAndPublishSnapshot"/>.
    /// </summary>
    [Benchmark]
    public void MovePlayers()
    {
        TeleportPlayers(NextOffset());
        _world.World.Index.Changes.Clear();
    }

    /// <summary>
    /// Moves 200 players as <see cref="MovePlayers"/> does and publishes the occupancy snapshot and the immutable player
    /// positions, as every tick does.
    /// </summary>
    [Benchmark]
    public void MovePlayersAndPublishSnapshot()
    {
        TeleportPlayers(NextOffset());
        _world.World.PublishSnapshot();
        _world.World.Index.Changes.Clear();
    }

    private void SetupStopped(PlayerSpread spread)
    {
        _world = BenchmarkWorld.Create(PlayerCount, ObjectCount, spread);
        _world.StopSimulation();
        _publishMovedPlayers = MoveAndPublishPlayers;
        _soundSnapshot = _world.Game.SoundSnapshotForTest;
        _door = _world.World.Objects.Values.First(entity => entity.Subject is Door);
    }

    private int NextOffset() => (_invocation++ & 1) == 0 ? 1 : -1;

    private void MovePlayersInIndex(ReadOnlySpan<PlayerEntity> players, int offset)
    {
        var index = _world.World.Index;
        foreach (var player in players)
        {
            player.Area = player.Area with { X = player.Area.X + offset };
            index.AddOrUpdate(player);
        }
    }

    private void TeleportPlayers(int offset)
    {
        foreach (var player in _world.Players)
        {
            _world.World.Teleport(player.Subject, player.Area.X + offset, null);
        }
    }

    private void UpdateViewSessionsAfterMoves()
    {
        MovePlayersInIndex(_world.Players, NextOffset());
        _world.Game.Views.Update(_world.Game.PublishVersion);
        _world.World.Index.Changes.Clear();
    }

    private GameVolumeTable BuildVolumeTableAfterMoves(bool isDoorToggled)
    {
        var index = _world.World.Index;
        MovePlayersInIndex(_world.Players, NextOffset());
        if (isDoorToggled)
        {
            _door.IsSoundBlocking = !_door.IsSoundBlocking;
            index.AddOrUpdate(_door);
        }

        index.Changes.Clear();
        _soundSnapshot = _soundSnapshot.TakeChanges(index);
        var positions = new HearingPosition[_world.Players.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = new HearingPosition(_world.Players[i].ClientId, _world.Players[i].Area);
        }

        return _volumeTableBuilder.Build(positions, _soundSnapshot);
    }

    // Leaves the index alone, which only the publish's own measurement may do.
    private void MoveAndPublishPlayers()
    {
        var offset = NextOffset();
        var facing = offset > 0 ? GameDirection.East : GameDirection.West;
        foreach (var player in _world.Players)
        {
            player.Area = player.Area with { X = player.Area.X + offset };
            player.Facing = facing;
            _world.World.RequestPublish(player);
        }

        _world.Game.PublisherForTest.Publish();
    }

    private void PlaceOrRemoveAndPublishObjectSet()
    {
        if (_placedTree is null)
        {
            _placedTree = new Tree { X = _placeArea.X + 1, Y = _placeArea.Y + 1 };
            _world.World.Place(_placedTree);
        }
        else
        {
            _world.World.Remove(_placedTree);
            _placedTree = null;
        }

        if (!_world.Game.PublisherForTest.TryPublishObjectSet())
        {
            throw new InvalidOperationException("The object set was not published.");
        }
    }

    private sealed class DiscardingSink : IGameViewSink
    {
        public static readonly DiscardingSink Instance = new();

        public bool IsBusy => false;

        public GameViewUpdate? LastUpdate { get; private set; }

        public void Deliver(GameViewUpdate update, GamePanelState panel) => LastUpdate = update;
    }
}
