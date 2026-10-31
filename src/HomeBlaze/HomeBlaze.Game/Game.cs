using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
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

    // Task 5 replaces this constructor with ones that take the clients and a logger, Task 6 adds the clock.
    public Game()
    {
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
    }

    partial void OnObjectsChanging(ref Dictionary<string, GameObject> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, GameObject>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        RequestIndexRebuild();
        return Task.CompletedTask;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
