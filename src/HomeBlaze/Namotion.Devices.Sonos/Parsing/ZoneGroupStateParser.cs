using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses the ZoneGroupState XML returned by GetZoneGroupState and sent in ZoneGroupTopology events.
/// </summary>
internal static class ZoneGroupStateParser
{
    /// <remarks>
    /// Throws <see cref="System.Xml.XmlException"/> for malformed XML and <see cref="FormatException"/> when a member lacks a required UUID or Location attribute.
    /// </remarks>
    internal static SonosTopology Parse(string xml)
    {
        var document = SonosXml.Parse(xml);
        var groups = new List<SonosTopologyGroup>();
        foreach (var groupElement in document.Descendants("ZoneGroup"))
        {
            if (CreateGroup(groupElement) is { } group)
            {
                groups.Add(group);
            }
        }

        return new SonosTopology(groups);
    }

    /// <summary>
    /// Returns the group with its players, null when no member is a player.
    /// </summary>
    private static SonosTopologyGroup? CreateGroup(XElement groupElement)
    {
        List<XElement>? playerElements = null;
        List<XElement>? invisibleMembers = null;
        foreach (var member in groupElement.Elements("ZoneGroupMember"))
        {
            if (IsInvisible(member))
            {
                (invisibleMembers ??= []).Add(member);
            }
            else if ((string?)member.Attribute("IsZoneBridge") != "1")
            {
                // A Boost or Bridge only extends the Sonos network and has no renderer to poll or control.
                (playerElements ??= []).Add(member);
            }
        }

        if (playerElements is null)
        {
            return null;
        }

        var players = CreatePlayers(playerElements, invisibleMembers);
        return new SonosTopologyGroup(
            (string?)groupElement.Attribute("ID") ?? string.Empty,
            (string?)groupElement.Attribute("Coordinator") ?? players[0].Uuid,
            players);
    }

    private static List<SonosTopologyPlayer> CreatePlayers(List<XElement> playerElements, List<XElement>? invisibleMembers)
    {
        // One index for all players of the group: searching the invisible members again for every player takes
        // quadratic time in a group of thousands.
        UuidIndex? partners = invisibleMembers is null ? null : new UuidIndex(invisibleMembers);
        var players = new List<SonosTopologyPlayer>(playerElements.Count);
        foreach (var member in playerElements)
        {
            players.Add(CreatePlayer(member, partners));
        }

        return players;
    }

    private static SonosTopologyPlayer CreatePlayer(XElement member, UuidIndex? partners)
    {
        var uuid = GetRequiredAttribute(member, "UUID");
        var satellites = new List<SonosTopologySatellite>();
        if (member.HasElements)
        {
            AddHomeTheaterSatellites(member, satellites);
        }

        // A stereo pair lists its second speaker as an invisible member of the same group.
        if (partners is { } index && (string?)member.Attribute("ChannelMapSet") is { } stereoMap &&
            index.FindNamed(stereoMap, SonosSatelliteRole.StereoPartner) is { } named)
        {
            foreach (var (position, _, role) in named)
            {
                satellites.Add(CreateSatellite(index.Take(position), role));
            }
        }

        return new SonosTopologyPlayer(
            uuid,
            (string?)member.Attribute("ZoneName") ?? string.Empty,
            GetBaseUri(member),
            (string?)member.Attribute("SoftwareVersion"),
            GetIsWireless(member),
            (string?)member.Attribute("MoreInfo"),
            satellites);
    }

    private static void AddHomeTheaterSatellites(XElement member, List<SonosTopologySatellite> satellites)
    {
        var elements = member.Elements("Satellite").ToList();
        var roles = new SonosSatelliteRole[elements.Count];
        Array.Fill(roles, SonosSatelliteRole.Other);

        UuidIndex? index = null;
        if (elements.Count > 0 && (string?)member.Attribute("HTSatChanMapSet") is { } homeTheaterMap)
        {
            index = new UuidIndex(elements);
            if (index.Value.FindNamed(homeTheaterMap, SonosSatelliteRole.Other) is { } named)
            {
                foreach (var (position, _, role) in named)
                {
                    roles[position] = role;
                }
            }
        }

        for (var position = 0; position < elements.Count; position++)
        {
            // Through the index, so that satellites with the same UUID get the same role.
            var element = elements[position];
            satellites.Add(CreateSatellite(element, roles[index?.GetPosition(element) ?? position]));
        }
    }

    private static SonosTopologySatellite CreateSatellite(XElement element, SonosSatelliteRole role) =>
        new(
            GetRequiredAttribute(element, "UUID"),
            (string?)element.Attribute("ZoneName") ?? string.Empty,
            GetBaseUri(element),
            (string?)element.Attribute("SoftwareVersion"),
            GetIsWireless(element),
            role);

    private static string GetRequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name)
        ?? throw new FormatException($"The {element.Name.LocalName} element is missing the required '{name}' attribute.");

    private static bool IsInvisible(XElement element) =>
        (string?)element.Attribute("Invisible") == "1";

    private static Uri GetBaseUri(XElement element)
    {
        var location = new Uri(GetRequiredAttribute(element, "Location"));
        return new UriBuilder(location.Scheme, location.Host, location.Port).Uri;
    }

    private static bool? GetIsWireless(XElement element) => (string?)element.Attribute("EthLink") switch
    {
        "1" => false,
        "0" => true,
        _ => null
    };

    /// <summary>
    /// Elements by their UUID, which finds the ones a channel map names in one walk of the map.
    /// </summary>
    private readonly struct UuidIndex
    {
        private readonly Dictionary<string, int> _positions;

        /// <remarks>Throws <see cref="FormatException"/> when an element lacks the UUID attribute.</remarks>
        internal UuidIndex(List<XElement> elements)
        {
            Elements = elements;
            _positions = new Dictionary<string, int>(elements.Count, StringComparer.Ordinal);
            for (var position = 0; position < elements.Count; position++)
            {
                // The last of several elements with one UUID stands for all of them. A real group never repeats a
                // UUID, and a satellite per repeat and player would grow with the square of the members.
                _positions[GetRequiredAttribute(elements[position], "UUID")] = position;
            }
        }

        internal List<XElement> Elements { get; }

        /// <summary>
        /// Returns the element at the position and removes its UUID, so no later channel map names it.
        /// </summary>
        internal XElement Take(int position)
        {
            // A real invisible member belongs to one room. Left in, a member that every player of a group names
            // would be read once per player.
            var element = Elements[position];
            _positions.Remove(GetRequiredAttribute(element, "UUID"));
            return element;
        }

        /// <summary>
        /// Returns the position of the element that stands for the UUID of <paramref name="element"/>.
        /// </summary>
        internal int GetPosition(XElement element) => _positions[GetRequiredAttribute(element, "UUID")];

        /// <summary>
        /// Returns the elements the channel map names, ordered by position and each once, with the role of the first
        /// entry that names it or <paramref name="defaultRole"/> when that entry has no role; null when it names none.
        /// </summary>
        internal List<(int Position, int Entry, SonosSatelliteRole Role)>? FindNamed(string channelMap, SonosSatelliteRole defaultRole)
        {
            List<(int Position, int Entry, SonosSatelliteRole Role)>? named = null;
            var positions = _positions.GetAlternateLookup<ReadOnlySpan<char>>();
            var map = channelMap.AsSpan();
            foreach (var range in map.Split(';'))
            {
                // "RINCON_A0000000000101400:LF,RF": the UUID ends at the first colon.
                var entry = map[range];
                var separator = entry.IndexOf(':');
                if (separator >= 0 && positions.TryGetValue(entry[..separator], out var position))
                {
                    named ??= [];
                    named.Add((position, named.Count, GetRole(entry[(separator + 1)..]) ?? defaultRole));
                }
            }

            if (named is { Count: > 1 })
            {
                // By position and then by entry, so the first entry of a repeated UUID is the one that stays.
                named.Sort();
                var count = 1;
                for (var index = 1; index < named.Count; index++)
                {
                    if (named[index].Position != named[count - 1].Position)
                    {
                        named[count++] = named[index];
                    }
                }

                named.RemoveRange(count, named.Count - count);
            }

            return named;
        }

        /// <summary>
        /// Returns the role the channels of a channel map entry give, such as <c>LR,LTR</c>; null when they name none.
        /// </summary>
        private static SonosSatelliteRole? GetRole(ReadOnlySpan<char> channels)
        {
            SonosSatelliteRole? role = null;
            foreach (var range in channels.Split(','))
            {
                switch (channels[range])
                {
                    case "SW":
                        return SonosSatelliteRole.Subwoofer;
                    case "LR":
                        role = SonosSatelliteRole.RearLeft;
                        break;
                    case "RR":
                        role ??= SonosSatelliteRole.RearRight;
                        break;
                }
            }

            return role;
        }
    }
}
