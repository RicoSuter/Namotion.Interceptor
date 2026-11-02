using HomeBlaze.Game.Buildings;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.View;
using HomeBlaze.Game.Voice;
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
    private static readonly TimeSpan ContinuationWait = TimeSpan.FromMilliseconds(20);

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
    /// Advances the clock by one tick interval and runs one tick on the test thread; waits for a save and a volume table
    /// build the tick started, so the next tick takes in their results.
    /// </summary>
    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            TimeProvider.Advance(GameSimulationSettings.TickInterval);
            RunTick();
        }
    }

    /// <summary>
    /// Runs one tick on the test thread without advancing the clock; waits for a save and a volume table build the tick
    /// started.
    /// </summary>
    public void RunTick()
    {
        Game.RunTickForTest();
        Game.SaveTask.GetAwaiter().GetResult();
        Game.VolumeTableBuildTask.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs ticks until the task completes, at most 100; only the first advances the clock.
    /// </summary>
    public async Task<T> RunAsync<T>(Task<T> task)
    {
        RunTicksUntilCompleted(task);
        return await task;
    }

    /// <summary>
    /// Runs ticks until the task completes, at most 100; only the first advances the clock.
    /// </summary>
    public async Task RunAsync(Task task)
    {
        RunTicksUntilCompleted(task);

        await task;
    }

    // A task that ends in a continuation completes a moment after its tick, so the later ticks wait for it briefly and
    // leave the clock alone: waiting must not move game time.
    private void RunTicksUntilCompleted(Task task)
    {
        if (task.IsCompleted)
        {
            return;
        }

        Tick();
        for (var i = 1; i < 100 && !((IAsyncResult)task).AsyncWaitHandle.WaitOne(ContinuationWait); i++)
        {
            RunTick();
        }
    }

    /// <summary>
    /// Runs ticks until a change made before is in the volume table: one volume table interval until a build starts,
    /// and the tick after it, which publishes the table that build made.
    /// </summary>
    public void BuildVolumeTable() => Tick((int)(GameVoiceSettings.VolumeTableInterval / GameSimulationSettings.TickInterval) + 1);

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
    /// Starts a requested save that writes only once the returned source completes, which pauses publishing meanwhile.
    /// Tick with <see cref="Game.RunTickForTest"/> until <see cref="ReleaseSaveAsync"/>, because <see cref="Tick"/> waits
    /// for the save.
    /// </summary>
    /// <exception cref="InvalidOperationException">No save was requested.</exception>
    public TaskCompletionSource HoldSave()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Root.WriteBlocker = blocker.Task;
        TimeProvider.Advance(GameSimulationSettings.SaveInterval);
        Game.RunTickForTest();
        if (Game.SaveTask.IsCompleted)
        {
            throw new InvalidOperationException("No save was requested.");
        }

        return blocker;
    }

    /// <summary>
    /// Lets the save held by <see cref="HoldSave"/> write and runs the tick that takes in its result and resumes publishing.
    /// </summary>
    public async Task ReleaseSaveAsync(TaskCompletionSource blocker)
    {
        Root.WriteBlocker = null;
        blocker.SetResult();
        await Game.SaveTask;
        Tick();
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

    /// <summary>
    /// Opens a view session for the client and runs the tick that delivers its first update.
    /// </summary>
    public GameViewSession OpenView(string? clientId, IGameViewSink sink)
    {
        var session = Game.Views.Open(clientId, sink);
        Tick();
        return session;
    }

    /// <summary>
    /// Gets the targets of the client's player as the panel of a new view session shows them.
    /// </summary>
    public IReadOnlyList<GameObject> GetTargets(string clientId)
    {
        var sink = new RecordingViewSink();
        using var session = OpenView(clientId, sink);
        return sink.Panel.Targets;
    }

    public Wall AddWall(int x, int y, int width = Wall.DefaultLength, int depth = 1) =>
        AddObject($"wall-{Game.GetObjects().Count}", new Wall { X = x, Y = y, Width = width, Depth = depth });

    public Door AddDoor(int x, int y, bool isOpen = false, int width = Door.DefaultLength, int depth = 1) =>
        AddObject($"door-{Game.GetObjects().Count}", new Door { X = x, Y = y, Width = width, Depth = depth, IsOpen = isOpen });

    /// <summary>
    /// Adds the object to the game under the name, as an edit of its region from outside does, and runs the tick that
    /// takes it over; publishes first, so the write starts from the objects the simulation holds.
    /// </summary>
    public T AddObject<T>(string name, T gameObject) where T : GameObject
    {
        Publish();
        var (regionX, regionY) = GameRegion.GetCoordinates(gameObject.X, gameObject.Y);
        var key = GameRegion.GetKey(regionX, regionY);
        if (Game.Regions.TryGetValue(key, out var region))
        {
            region.Objects = new Dictionary<string, GameObject>(region.Objects) { [name] = gameObject };
        }
        else
        {
            var added = new GameRegion { Objects = { [name] = gameObject } };
            Game.Regions = new Dictionary<string, GameRegion>(Game.Regions) { [key] = added };
        }

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
