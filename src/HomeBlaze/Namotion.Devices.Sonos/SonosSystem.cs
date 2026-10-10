using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Client;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A Sonos household: its room players, their bonded satellites and the groups they form.
/// </summary>
/// <remarks>
/// The state of the system, its players, satellites and groups changes, and raises its change notifications, while
/// internal locks are held that every Sonos operation and poll also takes. A change subscriber must therefore not
/// wait synchronously for a Sonos operation, for example with <c>GetAwaiter().GetResult()</c>, or that wait deadlocks;
/// it may start one without waiting for it.
/// </remarks>
[Category("Devices")]
[Description("Sonos household with its room players, satellites and groups")]
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "A device root aggregates its function subjects and the capability interfaces it implements; splitting it would only spread the same dependencies across files.")]
public partial class SonosSystem : BackgroundService,
    IConfigurable,
    IHubDevice,
    IMonitoredService,
    IConnectionState,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    /// <summary>
    /// The default <see cref="EventPort"/>.
    /// </summary>
    public const int DefaultEventPort = 6329;

    /// <summary>
    /// The default <see cref="PollingInterval"/> and <see cref="RetryInterval"/>, in seconds.
    /// </summary>
    public const int DefaultIntervalSeconds = 30;

    /// <summary>
    /// The shortest <see cref="PollingInterval"/> and <see cref="RetryInterval"/>, in seconds; a shorter one is raised to it.
    /// </summary>
    public const int MinimumIntervalSeconds = 5;

    /// <summary>
    /// The longest <see cref="PollingInterval"/> and <see cref="RetryInterval"/>, in seconds; a longer one is lowered to it.
    /// </summary>
    public const int MaximumIntervalSeconds = 3600;

    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(DefaultIntervalSeconds);

    private readonly ILogger<SonosSystem> _logger;

    // The last order NextOrder handed out.
    private long _order;

    /// <summary>
    /// Any speaker of the household as host or host:port. Empty tries the speakers found since the system started, then SSDP discovery.
    /// </summary>
    [Configuration]
    public partial string? SeedHost { get; set; }

    /// <summary>
    /// The address speakers send events to. Empty detects the local address that routes to the seed speaker.
    /// </summary>
    [Configuration]
    public partial string? EventCallbackHost { get; set; }

    /// <summary>
    /// The port the event listener binds to and speakers send events to.
    /// </summary>
    [Configuration]
    public partial int EventPort { get; set; }

    /// <summary>
    /// The time between reconciliations of topology, state and favorites, from 5 seconds to one hour. Zero or less uses 30 seconds.
    /// </summary>
    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    /// <summary>
    /// The delay before reconnecting after a failed connection, from 5 seconds to one hour. Zero or less uses 30 seconds.
    /// </summary>
    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    /// <summary>
    /// The room players by RINCON id. A player that leaves the topology stays and reports
    /// <see cref="SonosDevice.IsConnected"/> false. The dictionary is replaced, never mutated.
    /// </summary>
    [State(Position = 1)]
    public partial Dictionary<string, SonosPlayer> Players { get; internal set; }

    /// <summary>
    /// The current groups by the RINCON id of their coordinator; a standalone room is a group of one. The dictionary
    /// is replaced, never mutated.
    /// </summary>
    [State(Position = 2)]
    public partial Dictionary<string, SonosGroup> Groups { get; internal set; }

    /// <summary>
    /// The favorites <see cref="SonosPlayer.PlayFavoriteAsync"/> accepts by title. The array is replaced, never mutated.
    /// </summary>
    [State(Position = 3)]
    public partial SonosFavorite[] Favorites { get; internal set; }

    /// <summary>
    /// Whether the event listener runs and at least one subscription has delivered an event. While false, the state
    /// is only as current as the last poll.
    /// </summary>
    [State(Position = 4)]
    public partial bool AreEventsActive { get; internal set; }

    /// <summary>
    /// The host the speakers send events to while the event listener runs; null while it does not, because it could
    /// not listen, no local address routes to the seed speaker or the system is not connected. It stays set when no
    /// event arrives, which <see cref="AreEventsActive"/> reports.
    /// </summary>
    [State(Position = 5)]
    public partial string? ActiveEventCallbackHost { get; internal set; }

    public partial bool IsConnected { get; internal set; }

    public partial ServiceStatus Status { get; internal set; }

    public partial string? StatusMessage { get; internal set; }

    /// <summary>
    /// When the last reconciliation of topology, state and favorites completed; null before the first.
    /// </summary>
    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => "Sonos System";

    [Derived]
    public string? IconName => "LibraryMusic";

    [Derived]
    public string? IconColor
    {
        get
        {
            if (IsConnected)
            {
                return "Success";
            }

            return Status == ServiceStatus.Error ? "Error" : null;
        }
    }

    // Read by the connection loop in SonosSystem.Connection.cs.
    internal IHttpClientFactory HttpClientFactory { get; }

    /// <summary>
    /// The host the event listener binds to; "+" accepts events on every interface. Tests bind loopback only.
    /// </summary>
    internal string EventListenHost { get; init; } = "+";

    /// <summary>
    /// Searches the network for any speaker when neither SeedHost nor a known speaker answers. Tests replace it.
    /// </summary>
    internal Func<CancellationToken, Task<Uri?>> DiscoverSpeakerAsync { get; set; } = SonosDiscovery.FindSpeakerAsync;

    /// <summary>
    /// The clock for timestamps and scheduling. Tests replace it.
    /// </summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// The shortest polling and retry interval; a shorter configured value is raised to it. Tests lower it.
    /// </summary>
    internal TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(MinimumIntervalSeconds);

    /// <summary>
    /// The shortest subscription lifetime renewals are scheduled for, whatever a speaker grants. Tests shorten it.
    /// </summary>
    internal TimeSpan MinimumSubscriptionLifetime { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long after a speaker accepted a subscription its first event may take before a Warning reports that events
    /// cannot reach the callback. Tests shorten it.
    /// </summary>
    internal TimeSpan InitialEventTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a renewal that failed without an answer waits before it is tried again. Tests shorten it.
    /// </summary>
    internal TimeSpan FailedRenewalRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public SonosSystem(IHttpClientFactory httpClientFactory, ILogger<SonosSystem> logger)
    {
        HttpClientFactory = httpClientFactory;
        _logger = logger;

        EventPort = DefaultEventPort;
        PollingInterval = DefaultInterval;
        RetryInterval = DefaultInterval;
        Players = new Dictionary<string, SonosPlayer>(StringComparer.Ordinal);
        Groups = new Dictionary<string, SonosGroup>(StringComparer.Ordinal);
        Favorites = [];
        Status = ServiceStatus.Stopped;
    }

    [Derived]
    [PropertyAttribute("Refresh", KnownAttributes.IsEnabled)]
    public bool Refresh_IsEnabled => IsConnected;

    [Derived]
    [PropertyAttribute("GroupAll", KnownAttributes.IsEnabled)]
    public bool GroupAll_IsEnabled => IsConnected;

    [Derived]
    [PropertyAttribute("UngroupAll", KnownAttributes.IsEnabled)]
    public bool UngroupAll_IsEnabled => IsConnected;

    /// <summary>
    /// Reads topology, state and favorites now instead of at the next poll.
    /// </summary>
    [Operation(Title = "Refresh", Icon = "Refresh", Position = 1, Description = "Reads topology, state and favorites now instead of at the next poll.")]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await ReconcileAsync(cancellationToken);
    }

    /// <summary>
    /// Groups every connected room with the given room (party mode).
    /// </summary>
    /// <param name="room">The room name or UUID of the player that becomes the coordinator.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [Operation(Title = "Group All Rooms", Position = 2, Description = "Groups every connected room with the given room, by room name or UUID (party mode).")]
    public Task GroupAllAsync(string room, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        var coordinator = FindPlayer(room) ?? throw CreateUnknownRoomException(room, nameof(room));
        return GroupAllCoreAsync(coordinator, cancellationToken);
    }

    private async Task GroupAllCoreAsync(SonosPlayer coordinator, CancellationToken cancellationToken)
    {
        var coordinatorConnection = GetConnectionForCommand(coordinator.Uuid);
        await RunGroupingCommandsAsync(async token =>
        {
            // Only a coordinator can be joined, so a grouped target first becomes standalone.
            if (!coordinator.IsGroupCoordinator)
            {
                await coordinatorConnection.LeaveGroupAsync(token);
            }

            foreach (var player in Players.Values)
            {
                if (player.IsConnected && !ReferenceEquals(player, coordinator) && player.GroupCoordinatorUuid != coordinator.Uuid)
                {
                    await GetConnectionForCommand(player.Uuid).JoinAsync(coordinator.Uuid, token);
                }
            }
        }, () => Players.Values.All(player => !player.IsConnected || player.GroupKey == coordinator.Uuid), cancellationToken);
    }

    /// <summary>
    /// Makes every room standalone.
    /// </summary>
    [Operation(Title = "Ungroup All Rooms", Position = 3, Description = "Makes every room standalone.")]
    public async Task UngroupAllAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await RunGroupingCommandsAsync(async token =>
        {
            foreach (var player in Players.Values)
            {
                if (player.IsConnected && !player.IsGroupCoordinator)
                {
                    await GetConnectionForCommand(player.Uuid).LeaveGroupAsync(token);
                }
            }
        }, () => Players.Values.All(player => !player.IsConnected || player.IsGroupCoordinator), cancellationToken);
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw CreateNotConnectedException();
        }
    }

    // A not-connected error faults the returned task, like any other failure of a command, while argument and
    // capability validation (null or unknown names, a missing home theater or line-in) throws synchronously.
    internal InvalidOperationException CreateNotConnectedException() =>
        new("The Sonos system is not connected. " + (StatusMessage ?? "Waiting for the connection to be established."));

    /// <summary>
    /// Returns the next value of a sequence that orders polls against events: a poll takes one before it reads, an
    /// event when it arrives. Unlike a wall-clock time, a later call always returns a larger value.
    /// </summary>
    internal long NextOrder() => Interlocked.Increment(ref _order);

    internal SonosPlayer? FindPlayer(string roomNameOrUuid)
    {
        var players = Players;
        if (players.TryGetValue(roomNameOrUuid, out var byUuid))
        {
            return byUuid;
        }

        // A replaced speaker keeps its room name on the old, missing UUID, so a player in the topology wins.
        SonosPlayer? missingMatch = null;
        foreach (var player in players.Values)
        {
            if (string.Equals(player.RoomName, roomNameOrUuid, StringComparison.OrdinalIgnoreCase))
            {
                if (player.IsInTopology)
                {
                    return player;
                }

                missingMatch ??= player;
            }
        }

        return missingMatch;
    }

    internal SonosFavorite? FindFavorite(string title) =>
        Favorites.FirstOrDefault(favorite => string.Equals(favorite.Title, title, StringComparison.OrdinalIgnoreCase));

    internal void SetFavorites(IReadOnlyList<SonosFavorite> favorites)
    {
        if (!favorites.SequenceEqual(Favorites))
        {
            Favorites = [.. favorites];
        }
    }

    internal ArgumentException CreateUnknownRoomException(string value, string parameterName) =>
        new($"Unknown Sonos room '{value}'. Known rooms: {string.Join(", ", Players.Values.Select(player => player.RoomName))}.", parameterName);
}
