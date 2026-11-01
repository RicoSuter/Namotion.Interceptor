using HomeBlaze.Game.Buildings;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// A game in a root with a fake clock and fake clients, whose simulation ticks on the test thread: a test runs ticks
/// with <see cref="Tick"/> and <see cref="RunAsync{T}"/> instead of waiting.
/// </summary>
internal sealed class GameTestContext
{
    public FakeTimeProvider TimeProvider { get; } = new();

    public FakeClientDirectory Clients { get; } = new();

    public TestRoot Root { get; }

    public Game Game { get; }

    /// <param name="logger">The game's logger, none by default.</param>
    /// <param name="subjectContext">The context of the root, the app's context by default.</param>
    public GameTestContext(ILogger? logger = null, IInterceptorSubjectContext? subjectContext = null)
    {
        Root = new TestRoot(subjectContext ?? SubjectContextFactory.Create(new ServiceCollection()));
        Game = new Game(Clients, TimeProvider, logger ?? NullLogger.Instance);
        Root.Game = Game;
        Game.StartForTest();
        Tick();
    }

    /// <summary>
    /// Advances the clock by one tick interval and runs one tick on the test thread; waits for a save the tick started,
    /// so the next tick takes in its result.
    /// </summary>
    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            TimeProvider.Advance(GameSimulationSettings.TickInterval);
            Game.RunTickForTest();
            Game.SaveTask.GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Runs ticks until the task completes, at most 100.
    /// </summary>
    public async Task<T> RunAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            Tick();
        }

        return await task;
    }

    /// <summary>
    /// Runs ticks until the task completes, at most 100.
    /// </summary>
    public async Task RunAsync(Task task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            Tick();
        }

        await task;
    }

    /// <summary>
    /// Runs ticks until the publish interval passed, so subjects show the simulation's state.
    /// </summary>
    public void Publish() => Tick((int)(GameSimulationSettings.PublishInterval / GameSimulationSettings.TickInterval) + 1);

    /// <summary>
    /// Reconciles the players with the clients and publishes the result.
    /// </summary>
    public void Reconcile()
    {
        Game.PostReconcile();
        Tick();
        Publish();
    }

    /// <summary>
    /// Runs ticks until the save interval passed and one more, so a requested save is written and its result taken in.
    /// </summary>
    public Task SaveAsync()
    {
        Tick((int)(GameSimulationSettings.SaveInterval / GameSimulationSettings.TickInterval) + 2);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves the player of the client one step after the step interval passed, so the rate limit never refuses it.
    /// </summary>
    public Task<MoveResult?> StepAsync(string clientId, GameDirection direction, bool turnOnly = false)
    {
        TimeProvider.Advance(Game.StepInterval);
        return RunAsync(Game.StepAsync(clientId, direction, turnOnly));
    }

    /// <summary>
    /// Adds a connected client, anonymous unless <paramref name="isNamed"/>, and moves its player to the position.
    /// </summary>
    public GamePlayer AddConnectedPlayer(string clientId, int x = 0, int y = 0, bool isNamed = false)
    {
        Clients.Set(clientId, isNamed, isConnected: true);
        Reconcile();
        var player = Game.Players[clientId];
        player.X = x;
        player.Y = y;
        Tick();
        return player;
    }

    public Wall AddWall(int x, int y, int width = Wall.DefaultLength, int depth = 1) =>
        AddObject($"wall-{Game.Objects.Count}", new Wall { X = x, Y = y, Width = width, Depth = depth });

    public Door AddDoor(int x, int y, bool isOpen = false, int width = Door.DefaultLength, int depth = 1) =>
        AddObject($"door-{Game.Objects.Count}", new Door { X = x, Y = y, Width = width, Depth = depth, IsOpen = isOpen });

    /// <summary>
    /// Adds the object to the game under the name and runs the tick that takes it over; publishes first, so the write
    /// starts from the objects the simulation holds.
    /// </summary>
    public T AddObject<T>(string name, T gameObject) where T : GameObject
    {
        Publish();
        Game.Objects = new Dictionary<string, GameObject>(Game.Objects) { [name] = gameObject };
        Tick();
        return gameObject;
    }

    /// <summary>
    /// Adds the tool to the game under the name.
    /// </summary>
    public T AddTool<T>(string name, T tool) where T : GameTool
    {
        Game.Tools = new Dictionary<string, GameTool>(Game.Tools) { [name] = tool };
        return tool;
    }
}
