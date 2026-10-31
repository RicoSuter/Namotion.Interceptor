using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Clients;
using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game;

/// <summary>
/// A multiplayer world on an infinite grid of 25 cm tiles in which every browser client of the installation is a player.
/// </summary>
[Category("Game")]
[Description("A multiplayer world in which every browser client of the installation is a player")]
[DisplayName("Game")]
[SubjectIcon("SportsEsports")]
[InterceptorSubject]
public partial class Game : BackgroundService, IConfigurable, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The time a player takes for a step of one tile, about 1.4 m/s.
    /// </summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(180);

    // Guards Players, Objects, the positions of their objects and the index, so that two players never take one tile.
    private readonly Lock _lock = new();
    private readonly IGameClientDirectory _clients;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    // Guarded by _lock: an outside replacement of Players or Objects that a copy-on-write of the game starts from.
    private Dictionary<string, GamePlayer>? _pendingPlayers;
    private Dictionary<string, GameObject>? _pendingObjects;

    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// The players by client id; the game adds and removes them as the clients of the installation come and go.
    /// </summary>
    [Configuration]
    public partial Dictionary<string, GamePlayer> Players { get; set; }

    /// <summary>
    /// The objects by name.
    /// </summary>
    [Configuration]
    public partial Dictionary<string, GameObject> Objects { get; set; }

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
        Name = string.Empty;
        Players = new Dictionary<string, GamePlayer>();
        Objects = new Dictionary<string, GameObject>();
    }

    // Set before Players changes, so a player reachable through Players always knows its client.
    partial void OnPlayersChanging(ref Dictionary<string, GamePlayer> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, GamePlayer>();
        foreach (var (id, player) in newValue)
        {
            player.ClientId = id;
        }

        // A reload or the configuration editor assigns outside the lock, so a copy-on-write on another thread may read
        // Players before this write lands and assign its copy after it; it starts from this dictionary instead.
        if (!_lock.IsHeldByCurrentThread)
        {
            lock (_lock)
            {
                _pendingPlayers = newValue;
            }
        }
    }

    partial void OnObjectsChanging(ref Dictionary<string, GameObject> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, GameObject>();

        // See OnPlayersChanging.
        if (!_lock.IsHeldByCurrentThread)
        {
            lock (_lock)
            {
                _pendingObjects = newValue;
            }
        }
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        RequestIndexRebuild();
        SignalReconcile();
        return Task.CompletedTask;
    }

    // Callers hold _lock. The latest assigned players, also when an outside assignment has not landed yet; a pending
    // value is always the latest, because every write of the game clears it.
    private Dictionary<string, GamePlayer> CurrentPlayers => _pendingPlayers ?? Players;

    // Callers hold _lock.
    private Dictionary<string, GameObject> CurrentObjects => _pendingObjects ?? Objects;

    // Callers hold _lock.
    private void ReplacePlayers(Dictionary<string, GamePlayer> players)
    {
        _pendingPlayers = null;
        Players = players;
    }

    // Callers hold _lock.
    private void ReplaceObjects(Dictionary<string, GameObject> objects)
    {
        _pendingObjects = null;
        Objects = objects;
    }
}
