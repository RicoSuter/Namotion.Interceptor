using System.ComponentModel;
using System.Reactive.Concurrency;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Clients;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Game;

/// <summary>
/// A multiplayer world on an infinite grid of 25 cm tiles in which every browser client of the installation is a player.
/// </summary>
[Category("Game")]
[Description("A multiplayer world in which every browser client of the installation is a player")]
[DisplayName("Game")]
[SubjectIcon("SportsEsports")]
[InterceptorSubject]
public partial class Game : BackgroundService, IConfigurable, ITitleProvider, IIconProvider, IGameWorld
{
    /// <summary>
    /// The time a player takes for a step of one tile, about 1.4 m/s.
    /// </summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(180);

    private readonly IGameClientDirectory _clients;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly GameSimulation _simulation;
    private readonly GameWorldState _world;
    private readonly GamePublisher _publisher;

    private int _isStarted;

    private Task _lastRun = Task.CompletedTask;

    [ThreadStatic]
    private static bool t_isAssigningClientIds;

    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// The players by client id; the game adds and removes them as the clients of the installation come and go.
    /// </summary>
    [Configuration]
    public partial Dictionary<string, GamePlayer> Players { get; set; }

    /// <summary>
    /// The objects in regions of <see cref="GameRegion.Size"/> by <see cref="GameRegion.Size"/> tiles, by the key of the
    /// region, its coordinates as "{X}_{Y}"; the game keeps every object in the region of its south-west corner.
    /// </summary>
    [Configuration]
    public partial Dictionary<string, GameRegion> Regions { get; set; }

    /// <summary>
    /// The tools by name, whose operations the game page offers every player; the game never changes them.
    /// </summary>
    [Configuration]
    public partial Dictionary<string, GameTool> Tools { get; set; }

    [Derived]
    public string? Title => string.IsNullOrEmpty(Name) ? "Game" : Name;

    public string? IconName => "SportsEsports";

    public Game(ILogger<Game> logger)
        : this(null, TimeProvider.System, logger)
    {
    }

    internal Game(IGameClientDirectory? clients, TimeProvider timeProvider, ILogger logger)
    {
        _clients = clients ?? new InstallationClientDirectory(() => ((IInterceptorSubject)this).Context);
        _timeProvider = timeProvider;
        _logger = logger;
        _voiceMixer = new GameVoiceMixer(timeProvider, logger);
        _simulation = new GameSimulation(timeProvider, logger);
        _world = new GameWorldState(timeProvider, _voiceMixer);
        _publisher = new GamePublisher(this, _world, timeProvider);
        _voiceLoop = CreateVoiceLoop();
        _viewSessions = CreateViewSessions();
        _lastSaveTimestamp = timeProvider.GetTimestamp();
        Name = string.Empty;
        Players = new Dictionary<string, GamePlayer>();
        Regions = new Dictionary<string, GameRegion>();
        Tools = new Dictionary<string, GameTool>();

        // Index.Changes is cleared last, after everything that reads it in the tick; the view sessions come right before,
        // so they also see what the hooks of a save's publish changed. The object set is published and the objects'
        // hooks run first, so what they write is read again in the same tick.
        _simulation.AddTickHandler(PublishObjectSetWhenChanged);
        _simulation.AddTickHandler(_world.RefreshChangedSubjects);
        _simulation.AddTickHandler(_world.PublishSnapshot);
        _simulation.AddTickHandler(UpdateVoiceLoop);
        _simulation.AddTickHandler(UpdateVolumeTable);
        _simulation.AddTickHandler(PublishWhenDue);
        _simulation.AddTickHandler(StartSaveWhenDue);
        _simulation.AddTickHandler(CompleteWrittenPlacements);
        _simulation.AddTickHandler(UpdateViewSessions);
        _simulation.AddTickHandler(_world.Index.Changes.Clear);
    }

    /// <summary>
    /// Increases with every publish that wrote to the subjects; any thread may read it.
    /// </summary>
    internal long PublishVersion => _publisher.Version;

    /// <summary>
    /// Whether the calling thread runs this game's simulation.
    /// </summary>
    internal bool IsOnSimulationThread => _simulation.IsCurrent;

    /// <summary>
    /// Whether the calling thread assigns the client ids of players from their keys in <see cref="Players"/>, the one
    /// write of <see cref="GamePlayer.ClientId"/> that is allowed outside the simulation.
    /// </summary>
    internal static bool IsAssigningClientIds => t_isAssigningClientIds;

    partial void OnToolsChanging(ref Dictionary<string, GameTool> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, GameTool>();
    }

    // Set before Players changes, so a player reachable through Players always knows its client.
    partial void OnPlayersChanging(ref Dictionary<string, GamePlayer> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, GamePlayer>();
        var wasAssigningClientIds = t_isAssigningClientIds;
        t_isAssigningClientIds = true;
        try
        {
            foreach (var (clientId, player) in newValue)
            {
                if (player is not null && player.ClientId != clientId)
                {
                    player.ClientId = clientId;
                }
            }
        }
        finally
        {
            t_isAssigningClientIds = wasAssigningClientIds;
        }
    }

    partial void OnRegionsChanging(ref Dictionary<string, GameRegion> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, GameRegion>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        SignalReconcile();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers the write interceptor and queues the take-over of the subjects, without starting the loops.
    /// </summary>
    internal void StartForTest() => Start();

    /// <summary>
    /// Runs one tick of the simulation on the calling thread.
    /// </summary>
    internal void RunTickForTest() => _simulation.RunTick();

    /// <summary>
    /// The simulation, for benchmarks that measure its parts one by one.
    /// </summary>
    internal GameSimulation SimulationForTest => _simulation;

    /// <summary>
    /// The world state, for benchmarks that measure its parts one by one; touch it only on the simulation thread.
    /// </summary>
    internal GameWorldState WorldForTest => _world;

    /// <summary>
    /// The publisher, for benchmarks that measure its parts one by one; touch it only on the simulation thread.
    /// </summary>
    internal GamePublisher PublisherForTest => _publisher;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A stop that timed out leaves the former run finishing; this run waits for it, so two loops never tick at once.
        var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousRun = Interlocked.Exchange(ref _lastRun, run.Task);
        try
        {
            await previousRun.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await RunAsync(stoppingToken);
        }
        finally
        {
            run.SetResult();
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        Start();

        // Runs inside the writes of the whole graph, so it only signals.
        using var subscription = ((IInterceptorSubject)this).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (_clients.IsClientChange(change))
                {
                    SignalReconcile();
                }
            });

        // Stopped together with the simulation, also when that one ends on an unexpected exception.
        using var loopsStopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var voiceLoop = _voiceLoop.RunAsync(loopsStopping.Token);
        var housekeepingLoop = RunHousekeepingLoopAsync(loopsStopping.Token);
        try
        {
            await _simulation.RunAsync(stoppingToken);
        }
        finally
        {
            await loopsStopping.CancelAsync();
            await voiceLoop;
            await housekeepingLoop;
        }
    }

    // Placed objects are attached in the tick that adds them, so their World resolves once the command completes. Reads
    // the objects that schedules and commands of the tick wrote first, so an object that crossed a region boundary moves
    // to its new region in the same tick.
    private void PublishObjectSetWhenChanged()
    {
        _world.RefreshChangedSubjects();
        _publisher.TryPublishObjectSet();
        RunAddedToWorldOfPublishedObjects();
    }

    private void PublishWhenDue()
    {
        ResumePublishingWhenSaveIsOverdue();
        _publisher.TryPublish();
    }
}
