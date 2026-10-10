using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

public partial class SonosSystem
{
    // Topology arrives from the poll and from ZoneGroupTopology events, so applying it is serialized.
    private readonly Lock _topologyLock = new();

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
    /// Applies a ZoneGroupState read by a poll, unless a topology event was applied after the read started: the event
    /// is newer. Throws <see cref="System.Xml.XmlException"/> or <see cref="FormatException"/> when it cannot be parsed.
    /// </summary>
    /// <param name="zoneGroupState">The ZoneGroupState XML the poll read.</param>
    /// <param name="pollStartedAt">When the poll started, from <see cref="NextOrder"/>.</param>
    /// <param name="confirmsMissingPlayers">Whether the read counts toward the two that take a missing player offline, see <see cref="ApplyTopology"/>.</param>
    internal void ApplyPolledTopology(string zoneGroupState, long pollStartedAt, bool confirmsMissingPlayers = true)
    {
        var topology = ParseUnlessApplied(zoneGroupState);
        lock (_topologyLock)
        {
            if (!_topologyOrder.TryApplyPoll(pollStartedAt))
            {
                _logger.LogDebug("Skipped a polled Sonos topology that a topology event replaced while it was read.");
                return;
            }

            ApplyZoneGroupState(zoneGroupState, topology, confirmsMissingPlayers);
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
            if (ApplyZoneGroupState(zoneGroupState, topology, confirmsMissingPlayers: true))
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
    private bool ApplyZoneGroupState(string zoneGroupState, SonosTopology? topology, bool confirmsMissingPlayers)
    {
        if (zoneGroupState == _appliedZoneGroupState)
        {
            // Applying the same topology again changes nothing but this, see ApplyTopology.
            _unconfirmedMissingPlayers.Clear();
            return true;
        }

        if (!ApplyTopology(topology ?? ZoneGroupStateParser.Parse(zoneGroupState), confirmsMissingPlayers))
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
    /// <param name="topology">The topology to apply.</param>
    /// <param name="confirmsMissingPlayers">
    /// Whether the read counts toward the two that take a missing player offline. A read that does not is skipped
    /// while it misses players.
    /// </param>
    /// <returns>Whether the topology was applied.</returns>
    internal bool ApplyTopology(SonosTopology topology, bool confirmsMissingPlayers = true)
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
            if (_hasConnectionTopology && missing.Count > 0)
            {
                if (!confirmsMissingPlayers)
                {
                    return false;
                }

                if (!missing.IsSubsetOf(_unconfirmedMissingPlayers))
                {
                    _unconfirmedMissingPlayers = missing;
                    _logger.LogDebug("The Sonos topology misses {Players}; it is applied once the next read confirms it.", string.Join(", ", missing));
                    return false;
                }
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

            group.Update(topologyGroup.Players.Select(member => players[member.Uuid]).ToArray());
            updatedGroups[topologyGroup.CoordinatorUuid] = group;
        }

        if (!HaveSameEntries(groups, updatedGroups))
        {
            Groups = updatedGroups;
        }
    }

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
