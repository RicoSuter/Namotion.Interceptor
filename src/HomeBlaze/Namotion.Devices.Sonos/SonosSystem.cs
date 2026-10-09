using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A Sonos household: its room players, their bonded satellites and the groups they form.
/// </summary>
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
    internal const int DefaultEventPort = 6329;
    internal static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<SonosSystem> _logger;

    // Topology arrives from the poll and from ZoneGroupTopology events, so applying it is serialized.
    private readonly Lock _topologyLock = new();
    private volatile IReadOnlyList<SonosFavorite> _favorites = [];

    /// <summary>
    /// Any speaker of the household as host or host:port. Empty uses the last known speakers, then SSDP.
    /// </summary>
    [Configuration]
    public partial string? SeedHost { get; set; }

    /// <summary>
    /// The address speakers send events to. Empty detects the local address that routes to the seed speaker.
    /// </summary>
    [Configuration]
    public partial string? EventCallbackHost { get; set; }

    [Configuration]
    public partial int EventPort { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    [State(Position = 1)]
    public partial Dictionary<string, SonosPlayer> Players { get; internal set; }

    [State(Position = 2)]
    public partial Dictionary<string, SonosGroup> Groups { get; internal set; }

    /// <summary>
    /// The names of the favorites <c>SonosPlayer.PlayFavoriteAsync</c> accepts.
    /// </summary>
    [State(Position = 3)]
    public partial string[] Favorites { get; internal set; }

    [State(Position = 4)]
    public partial bool AreEventsActive { get; internal set; }

    [State(Position = 5)]
    public partial string? ActiveEventCallbackHost { get; internal set; }

    public partial bool IsConnected { get; internal set; }

    public partial ServiceStatus Status { get; internal set; }

    public partial string? StatusMessage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => "Sonos System";

    [Derived]
    public string? IconName => "LibraryMusic";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : Status == ServiceStatus.Error ? "Error" : null;

    // Read by the connection loop in SonosSystem.Runtime.cs.
    internal IHttpClientFactory HttpClientFactory { get; }

    public SonosSystem(IHttpClientFactory httpClientFactory, ILogger<SonosSystem> logger)
    {
        HttpClientFactory = httpClientFactory;
        _logger = logger;

        EventPort = DefaultEventPort;
        PollingInterval = DefaultPollingInterval;
        RetryInterval = DefaultRetryInterval;
        Players = new Dictionary<string, SonosPlayer>(StringComparer.Ordinal);
        Groups = new Dictionary<string, SonosGroup>(StringComparer.Ordinal);
        Favorites = [];
        Status = ServiceStatus.Stopped;
    }

    internal void ApplyTopology(SonosTopology topology)
    {
        lock (_topologyLock)
        {
            var players = Players;
            Dictionary<string, SonosPlayer>? updatedPlayers = null;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in topology.Groups)
            {
                foreach (var topologyPlayer in group.Players)
                {
                    present.Add(topologyPlayer.Uuid);
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

            foreach (var (uuid, player) in players)
            {
                if (!present.Contains(uuid))
                {
                    player.MarkMissing();
                    foreach (var satellite in player.Satellites.Values)
                    {
                        satellite.MarkMissing();
                    }
                }
            }

            if (updatedPlayers is not null)
            {
                Players = updatedPlayers;
            }

            ApplyGroups(topology, Players);
        }
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

        return players.Values.FirstOrDefault(player =>
            string.Equals(player.RoomName, roomNameOrUuid, StringComparison.OrdinalIgnoreCase));
    }

    internal SonosFavorite? FindFavorite(string name) =>
        _favorites.FirstOrDefault(favorite => string.Equals(favorite.Title, name, StringComparison.OrdinalIgnoreCase));

    internal void SetFavorites(IReadOnlyList<SonosFavorite> favorites)
    {
        _favorites = favorites;
        var names = favorites.Select(favorite => favorite.Title).ToArray();
        if (!names.SequenceEqual(Favorites))
        {
            Favorites = names;
        }
    }

    internal ArgumentException CreateUnknownRoomException(string value, string parameterName) =>
        new($"Unknown Sonos room '{value}'. Known rooms: {string.Join(", ", Players.Values.Select(player => player.RoomName))}.", parameterName);

    private static bool HaveSameEntries<T>(Dictionary<string, T> existing, Dictionary<string, T> updated)
        where T : class
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
