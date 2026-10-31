using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// A game in a root with a fake clock and fake clients.
/// </summary>
internal sealed class GameTestContext
{
    public FakeTimeProvider TimeProvider { get; } = new();

    public FakeClientDirectory Clients { get; } = new();

    public TestRoot Root { get; }

    public Game Game { get; }

    public GameTestContext()
    {
        Root = new TestRoot(SubjectContextFactory.Create(new ServiceCollection()));
        Game = new Game(Clients, TimeProvider, NullLogger.Instance);
        Root.Game = Game;
    }

    /// <summary>
    /// Adds a connected anonymous client and moves its player to the position.
    /// </summary>
    public GamePlayer AddConnectedPlayer(string clientId, int x = 0, int y = 0)
    {
        Clients.Set(clientId, isNamed: false, isConnected: true);
        Game.ReconcilePlayers();
        var player = Game.Players[clientId];
        player.X = x;
        player.Y = y;
        Game.RequestIndexRebuild();
        return player;
    }

    public Wall AddWall(int x, int y, int width = Wall.DefaultLength, int depth = 1)
    {
        var wall = new Wall { X = x, Y = y, Width = width, Depth = depth };
        Game.Objects = new Dictionary<string, GameObject>(Game.Objects) { [$"wall-{Game.Objects.Count}"] = wall };
        Game.RequestIndexRebuild();
        return wall;
    }

    public Door AddDoor(int x, int y, bool isOpen = false, int width = Door.DefaultLength, int depth = 1)
    {
        var door = new Door { X = x, Y = y, Width = width, Depth = depth, IsOpen = isOpen };
        Game.Objects = new Dictionary<string, GameObject>(Game.Objects) { [$"door-{Game.Objects.Count}"] = door };
        Game.RequestIndexRebuild();
        return door;
    }
}
