using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;
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

    // Topology arrives from the poll and from ZoneGroupTopology events, so applying it is serialized.
    private readonly Lock _topologyLock = new();

    // The last order NextOrder handed out.
    private long _order;

    // Guarded by _topologyLock. The order of the applied topology events orders a polled topology against them, and
    // the players missing from the last topology that was not applied wait for a second read to confirm them. The
    // first topology of a connection needs no confirmation: the previous one may be arbitrarily old.
    private PollEventOrder _topologyOrder = new();
    private HashSet<string> _unconfirmedMissingPlayers = new(StringComparer.Ordinal);
    private bool _hasConnectionTopology;

    // The raw ZoneGroupState of the applied topology, so an unchanged one, which is what nearly every poll and event
    // carries, is neither parsed nor applied again. Null when unknown. Written under _topologyLock.
    private string? _appliedZoneGroupState;

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

    // Read by the connection loop in SonosSystem.Runtime.cs.
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
        }, cancellationToken);
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
        }, cancellationToken);
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

    /// <summary>
    /// Applies a ZoneGroupState read by a poll, unless a topology event was applied after the read started: the event
    /// is newer. Throws <see cref="System.Xml.XmlException"/> or <see cref="FormatException"/> when it cannot be parsed.
    /// </summary>
    /// <param name="zoneGroupState">The ZoneGroupState XML the poll read.</param>
    /// <param name="pollStartedAt">When the poll started, from <see cref="NextOrder"/>.</param>
    internal void ApplyPolledTopology(string zoneGroupState, long pollStartedAt)
    {
        var topology = ParseUnlessApplied(zoneGroupState);
        lock (_topologyLock)
        {
            if (!_topologyOrder.TryApplyPoll(pollStartedAt))
            {
                _logger.LogDebug("Skipped a polled Sonos topology that a topology event replaced while it was read.");
                return;
            }

            ApplyZoneGroupState(zoneGroupState, topology);
        }
    }

    /// <summary>
    /// Applies a ZoneGroupState from a topology event. Throws <see cref="System.Xml.XmlException"/> or
    /// <see cref="FormatException"/> when it cannot be parsed.
    /// </summary>
    internal void ApplyTopologyEvent(string zoneGroupState)
    {
        var topology = ParseUnlessApplied(zoneGroupState);
        lock (_topologyLock)
        {
            if (ApplyZoneGroupState(zoneGroupState, topology))
            {
                _topologyOrder.RecordEvent(NextOrder());
            }
        }
    }

    /// <summary>
    /// Makes the next topology the first of a new connection, which is applied without confirming missing players.
    /// </summary>
    internal void ResetConnectionTopology()
    {
        lock (_topologyLock)
        {
            _hasConnectionTopology = false;
            _unconfirmedMissingPlayers.Clear();
            _appliedZoneGroupState = null;
        }
    }

    // Parsed outside _topologyLock; null for the applied ZoneGroupState, which ApplyZoneGroupState then skips, or
    // parses after all when another one was applied in between.
    private SonosTopology? ParseUnlessApplied(string zoneGroupState) =>
        zoneGroupState == Volatile.Read(ref _appliedZoneGroupState) ? null : ZoneGroupStateParser.Parse(zoneGroupState);

    // Caller holds _topologyLock.
    private bool ApplyZoneGroupState(string zoneGroupState, SonosTopology? topology)
    {
        if (zoneGroupState == _appliedZoneGroupState)
        {
            // Applying the same topology again changes nothing but this, see ApplyTopology.
            _unconfirmedMissingPlayers.Clear();
            return true;
        }

        if (!ApplyTopology(topology ?? ZoneGroupStateParser.Parse(zoneGroupState)))
        {
            return false;
        }

        Volatile.Write(ref _appliedZoneGroupState, zoneGroupState);
        return true;
    }

    /// <summary>
    /// Applies a topology. One that misses players of the current topology is applied only when the read before it
    /// missed them too, since a seed that just rebooted or woke up may briefly report only part of the household.
    /// </summary>
    /// <returns>Whether the topology was applied.</returns>
    internal bool ApplyTopology(SonosTopology topology)
    {
        lock (_topologyLock)
        {
            var players = Players;
            var present = topology.Groups
                .SelectMany(group => group.Players)
                .Select(player => player.Uuid)
                .ToHashSet(StringComparer.Ordinal);

            var missing = players.Values
                .Where(player => player.IsInTopology && !present.Contains(player.Uuid))
                .Select(player => player.Uuid)
                .ToHashSet(StringComparer.Ordinal);
            if (_hasConnectionTopology && missing.Count > 0 && !missing.IsSubsetOf(_unconfirmedMissingPlayers))
            {
                _unconfirmedMissingPlayers = missing;
                _logger.LogDebug("The Sonos topology misses {Players}; it is applied once the next read confirms it.", string.Join(", ", missing));
                return false;
            }

            _unconfirmedMissingPlayers.Clear();
            _hasConnectionTopology = true;
            Volatile.Write(ref _appliedZoneGroupState, null);
            var updatedPlayers = ApplyPlayerTopologies(topology, players);
            foreach (var device in GetDevices(players.Values.Where(player => !present.Contains(player.Uuid))))
            {
                device.MarkMissing();
            }

            if (updatedPlayers is not null)
            {
                Players = updatedPlayers;
            }

            ApplyGroups(topology, Players);
            return true;
        }
    }

    // Caller holds _topologyLock. Returns the players with the new ones added, or null when none are new.
    private Dictionary<string, SonosPlayer>? ApplyPlayerTopologies(SonosTopology topology, Dictionary<string, SonosPlayer> players)
    {
        Dictionary<string, SonosPlayer>? updatedPlayers = null;
        foreach (var group in topology.Groups)
        {
            foreach (var topologyPlayer in group.Players)
            {
                if (!players.TryGetValue(topologyPlayer.Uuid, out var player))
                {
                    player = new SonosPlayer(this, topologyPlayer.Uuid);
                    updatedPlayers ??= new Dictionary<string, SonosPlayer>(players, StringComparer.Ordinal);
                    updatedPlayers[topologyPlayer.Uuid] = player;
                    _logger.LogInformation("Found the Sonos player {Room} ({Uuid}).", topologyPlayer.RoomName, topologyPlayer.Uuid);
                }

                player.ApplyPlayerTopology(topologyPlayer, group.CoordinatorUuid);
            }
        }

        return updatedPlayers;
    }

    // Caller holds _topologyLock.
    private void ApplyGroups(SonosTopology topology, Dictionary<string, SonosPlayer> players)
    {
        var groups = Groups;
        var updatedGroups = new Dictionary<string, SonosGroup>(StringComparer.Ordinal);
        foreach (var topologyGroup in topology.Groups)
        {
            if (!players.TryGetValue(topologyGroup.CoordinatorUuid, out var coordinator))
            {
                continue;
            }

            if (!groups.TryGetValue(topologyGroup.CoordinatorUuid, out var group))
            {
                group = new SonosGroup(this, coordinator);
            }

            group.Update(topologyGroup.Id, topologyGroup.Players.Select(member => players[member.Uuid]).ToArray());
            updatedGroups[topologyGroup.CoordinatorUuid] = group;
        }

        if (!HaveSameEntries(groups, updatedGroups))
        {
            Groups = updatedGroups;
        }
    }

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

    /// <summary>
    /// Returns each player followed by its satellites.
    /// </summary>
    internal static IEnumerable<SonosDevice> GetDevices(IEnumerable<SonosPlayer> players)
    {
        foreach (var player in players)
        {
            yield return player;
            foreach (var satellite in player.Satellites.Values)
            {
                yield return satellite;
            }
        }
    }

    private static bool HaveSameEntries(Dictionary<string, SonosGroup> existing, Dictionary<string, SonosGroup> updated)
    {
        if (existing.Count != updated.Count)
        {
            return false;
        }

        foreach (var (key, value) in existing)
        {
            if (!updated.TryGetValue(key, out var updatedValue) || !ReferenceEquals(value, updatedValue))
            {
                return false;
            }
        }

        return true;
    }
}
