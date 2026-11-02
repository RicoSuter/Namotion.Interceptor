using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.View;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Benchmark;

/// <summary>
/// How <see cref="BenchmarkWorld"/> spreads the players.
/// </summary>
public enum PlayerSpread
{
    /// <summary>
    /// Groups of about 10 players near each other, far enough apart that no two groups see each other.
    /// </summary>
    Groups,

    /// <summary>
    /// All players within 120 by 120 tiles, so every player sees every other.
    /// </summary>
    Clustered
}

/// <summary>
/// A game in a root with a fake clock and connected fake clients, whose simulation ticks on the calling thread: the demo
/// world's walls, doors and trees tiled until the object count is reached, the players spread over it with room to step
/// east, and a view session per player whose sink is never busy and discards the updates.
/// </summary>
internal sealed class BenchmarkWorld
{
    private const int PlayersPerGroup = 10;

    // Players stand on a lattice whose cells hold a player and the tile it steps east to, with a free tile around.
    private const int SpotSpacingX = 4;
    private const int SpotSpacingY = 3;

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly BenchmarkClientDirectory _clients = new();
    private readonly string[] _clientIds;
    private readonly int _gridColumns;
    private readonly int _gridRows;
    private int _stepCount;

    private BenchmarkWorld(int players, int objects)
    {
        _clientIds = [.. Enumerable.Range(0, players).Select(index => $"client-{index:D3}")];
        var copies = (objects + BenchmarkLayout.ObjectCount - 1) / BenchmarkLayout.ObjectCount;
        _gridColumns = (int)Math.Ceiling(Math.Sqrt(copies));
        _gridRows = (copies + _gridColumns - 1) / _gridColumns;
        ObjectCount = objects;
        Root = new BenchmarkRoot(SubjectContextFactory.Create(new ServiceCollection()));
        Game = new Game(_clients, _timeProvider, NullLogger.Instance);
        Root.Game = Game;
    }

    public BenchmarkRoot Root { get; }

    public Game Game { get; }

    public int ObjectCount { get; }

    /// <summary>
    /// The client ids in the order of their players' spots: with <see cref="PlayerSpread.Groups"/> the first 11 form the
    /// first group, each further 10 the next one.
    /// </summary>
    public IReadOnlyList<string> ClientIds => _clientIds;

    public GameWorldState World => Game.WorldForTest;

    /// <summary>
    /// The player entities in the order of <see cref="ClientIds"/>.
    /// </summary>
    public PlayerEntity[] Players { get; private set; } = [];

    /// <summary>
    /// Builds the world, places the players and opens their view sessions, each of which has delivered its first update.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world did not end up as intended.</exception>
    public static BenchmarkWorld Create(int players, int objects, PlayerSpread spread = PlayerSpread.Groups)
    {
        var world = new BenchmarkWorld(players, objects);
        world.Game.StartForTest();
        world.Tick();

        world.Game.Regions = GameRegion.CreateRegions(world.CreateObjectSet());
        world.Tick();
        if (world.World.Objects.Count != objects)
        {
            throw new InvalidOperationException($"The world holds {world.World.Objects.Count} objects instead of {objects}.");
        }

        world.AddPlayers(spread);
        foreach (var clientId in world._clientIds)
        {
            world.Game.Views.Open(clientId, DiscardingViewSink.Instance);
        }

        // The sessions refresh their whole window at most a few per tick.
        world.Tick(players / GameViewSessions.MaximumFullRefreshesPerTick + 2);
        world.VerifyAllPlayersStep();
        return world;
    }

    /// <summary>
    /// Creates a new set of the tiled objects, with new subjects.
    /// </summary>
    public Dictionary<string, GameObject> CreateObjectSet()
    {
        var objects = new Dictionary<string, GameObject>(ObjectCount);
        for (var copy = 0; objects.Count < ObjectCount; copy++)
        {
            var offsetX = copy % _gridColumns * BenchmarkLayout.CopyWidth;
            var offsetY = copy / _gridColumns * BenchmarkLayout.CopyDepth;
            for (var index = 0; index < BenchmarkLayout.ObjectCount && objects.Count < ObjectCount; index++)
            {
                objects[$"object-{copy}-{index}"] = BenchmarkLayout.Create(index, offsetX, offsetY);
            }
        }

        return objects;
    }

    /// <summary>
    /// Advances the clock by one tick interval and runs a tick, as often as asked; waits for a save the tick started, so
    /// the next tick takes in its result.
    /// </summary>
    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            _timeProvider.Advance(GameSimulationSettings.TickInterval);
            RunTick();
        }
    }

    /// <summary>
    /// Posts a step of every player, east and west in turns so the players stay in place, after advancing the clock by
    /// <see cref="Game.StepInterval"/> so the rate limit allows it, and runs one tick.
    /// </summary>
    public void TickWithAllPlayersStepping() => PostStepsAndRunTick();

    /// <summary>
    /// Stops the simulation, so the parts can be measured one by one on the calling thread through
    /// <see cref="RunOnSimulation"/>; no tick runs anymore.
    /// </summary>
    public void StopSimulation() => Game.SimulationForTest.Stop();

    /// <summary>
    /// Runs the action on the calling thread as if on the simulation thread; call <see cref="StopSimulation"/> first.
    /// </summary>
    public void RunOnSimulation(Action action) => Game.SimulationForTest.RunExclusive(action);

    /// <summary>
    /// Gets an area of the size, free of movement blocking entities as of the last tick, near the tile.
    /// </summary>
    /// <exception cref="InvalidOperationException">No such area lies within 100 tiles.</exception>
    public GameArea FindFreeArea(int x, int y, int width, int depth)
    {
        foreach (var area in EnumerateFreeSpots(x - 50, y - 50, 100, 100, width, depth))
        {
            return area;
        }

        throw new InvalidOperationException($"No free area near {x}, {y}.");
    }

    private void RunTick()
    {
        Game.RunTickForTest();
        Game.SaveTask.GetAwaiter().GetResult();
    }

    private Task<MoveResult?>[] PostStepsAndRunTick()
    {
        var direction = (_stepCount++ & 1) == 0 ? GameDirection.East : GameDirection.West;
        _timeProvider.Advance(Game.StepInterval);
        var steps = new Task<MoveResult?>[_clientIds.Length];
        for (var i = 0; i < _clientIds.Length; i++)
        {
            steps[i] = Game.StepAsync(_clientIds[i], direction, turnOnly: false);
        }

        RunTick();
        return steps;
    }

    // Runs two stepping ticks, so the measured ticks start from the same place and facing as later even ones.
    private void VerifyAllPlayersStep()
    {
        for (var i = 0; i < 2; i++)
        {
            var steps = PostStepsAndRunTick();
            var refused = steps.Count(step => step.GetAwaiter().GetResult() is not { IsAccepted: true });
            if (refused > 0)
            {
                throw new InvalidOperationException($"{refused} of {steps.Length} players could not step.");
            }
        }
    }

    private void AddPlayers(PlayerSpread spread)
    {
        foreach (var clientId in _clientIds)
        {
            _clients.Add(clientId);
        }

        Game.PostReconcile();
        Tick();
        Publish();

        var spots = spread == PlayerSpread.Groups ? FindGroupSpots() : FindClusteredSpots();
        for (var i = 0; i < _clientIds.Length; i++)
        {
            var player = Game.Players[_clientIds[i]];
            player.X = spots[i].X;
            player.Y = spots[i].Y;
        }

        Tick();
        Publish();
        Players = [.. _clientIds.Select(clientId => World.Players[clientId])];
        for (var i = 0; i < Players.Length; i++)
        {
            if (Players[i].Area.X != spots[i].X || Players[i].Area.Y != spots[i].Y || !Players[i].IsConnected)
            {
                throw new InvalidOperationException($"The player of {_clientIds[i]} is not connected at its spot.");
            }
        }
    }

    // The first group holds one player more, so a session of its first player sees 10 others.
    private List<GameArea> FindGroupSpots()
    {
        var groupCount = Math.Max(1, (_clientIds.Length - 1) / PlayersPerGroup);
        var groupColumns = (int)Math.Ceiling(Math.Sqrt(groupCount));
        var groupRows = (groupCount + groupColumns - 1) / groupColumns;
        var width = _gridColumns * BenchmarkLayout.CopyWidth;
        var depth = _gridRows * BenchmarkLayout.CopyDepth;
        var spots = new List<GameArea>(_clientIds.Length);
        for (var group = 0; spots.Count < _clientIds.Length; group++)
        {
            var size = group == 0 ? PlayersPerGroup + 1 : Math.Min(PlayersPerGroup, _clientIds.Length - spots.Count);
            var centerX = (int)((group % groupColumns + 0.5) * width / groupColumns);
            var centerY = (int)((group / groupColumns % groupRows + 0.5) * depth / groupRows);
            spots.AddRange(EnumerateFreeSpots(centerX - 30, centerY - 30, 60, 60, GamePlayer.Size + 1, GamePlayer.Size).Take(size));
        }

        return spots;
    }

    private List<GameArea> FindClusteredSpots()
    {
        var centerX = _gridColumns * BenchmarkLayout.CopyWidth / 2;
        var centerY = _gridRows * BenchmarkLayout.CopyDepth / 2;
        var spots = EnumerateFreeSpots(centerX - 60, centerY - 60, 120, 120, GamePlayer.Size + 1, GamePlayer.Size)
            .Take(_clientIds.Length)
            .ToList();
        if (spots.Count < _clientIds.Length)
        {
            throw new InvalidOperationException($"Only {spots.Count} players fit into the cluster.");
        }

        return spots;
    }

    // The lattice keeps the spots apart, so only the objects need checking.
    private IEnumerable<GameArea> EnumerateFreeSpots(int x, int y, int width, int depth, int spotWidth, int spotDepth)
    {
        for (var spotY = y; spotY + spotDepth <= y + depth; spotY += SpotSpacingY)
        {
            for (var spotX = x; spotX + spotWidth <= x + width; spotX += SpotSpacingX)
            {
                var area = new GameArea(spotX, spotY, spotWidth, spotDepth);
                if (World.Snapshot.IsFree(area, except: null))
                {
                    yield return area;
                }
            }
        }
    }

    private void Publish() => Tick((int)(GameSimulationSettings.PublishInterval / GameSimulationSettings.TickInterval) + 1);

    private sealed class DiscardingViewSink : IGameViewSink
    {
        public static readonly DiscardingViewSink Instance = new();

        public bool IsBusy => false;

        public void Deliver(GameViewUpdate update, GamePanelState panel)
        {
            // Discarded: the benchmark measures the simulation, not the page.
        }
    }
}
