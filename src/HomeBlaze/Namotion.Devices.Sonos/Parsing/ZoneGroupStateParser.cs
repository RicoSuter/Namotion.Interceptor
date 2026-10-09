using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses the ZoneGroupState XML returned by GetZoneGroupState and sent in ZoneGroupTopology events.
/// </summary>
internal static class ZoneGroupStateParser
{
    internal static SonosTopology Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var groups = new List<SonosTopologyGroup>();
        foreach (var groupElement in document.Descendants("ZoneGroup"))
        {
            var members = groupElement.Elements("ZoneGroupMember").ToList();
            var players = new List<SonosTopologyPlayer>();
            foreach (var member in members)
            {
                if (IsInvisible(member))
                {
                    continue;
                }

                var uuid = (string)member.Attribute("UUID")!;
                var satellites = new List<SonosTopologySatellite>();

                var homeTheaterMap = (string?)member.Attribute("HTSatChanMapSet");
                foreach (var satellite in member.Elements("Satellite"))
                {
                    satellites.Add(CreateSatellite(satellite, homeTheaterMap, isStereoPair: false));
                }

                // A stereo pair lists its second speaker as an invisible member of the same group.
                var stereoMap = (string?)member.Attribute("ChannelMapSet");
                foreach (var partner in members)
                {
                    if (IsInvisible(partner) && ContainsUuid(stereoMap, (string)partner.Attribute("UUID")!))
                    {
                        satellites.Add(CreateSatellite(partner, stereoMap, isStereoPair: true));
                    }
                }

                players.Add(new SonosTopologyPlayer(
                    uuid,
                    (string?)member.Attribute("ZoneName") ?? string.Empty,
                    GetBaseUri(member),
                    (string?)member.Attribute("SoftwareVersion"),
                    GetIsWireless(member),
                    (string?)member.Attribute("MoreInfo"),
                    satellites));
            }

            if (players.Count > 0)
            {
                groups.Add(new SonosTopologyGroup(
                    (string?)groupElement.Attribute("ID") ?? string.Empty,
                    (string?)groupElement.Attribute("Coordinator") ?? players[0].Uuid,
                    players));
            }
        }

        return new SonosTopology(groups);
    }

    private static SonosTopologySatellite CreateSatellite(XElement element, string? channelMap, bool isStereoPair)
    {
        var uuid = (string)element.Attribute("UUID")!;
        return new SonosTopologySatellite(
            uuid,
            (string?)element.Attribute("ZoneName") ?? string.Empty,
            GetBaseUri(element),
            (string?)element.Attribute("SoftwareVersion"),
            GetIsWireless(element),
            GetRole(channelMap, uuid, isStereoPair));
    }

    private static bool IsInvisible(XElement element) =>
        (string?)element.Attribute("Invisible") == "1";

    private static Uri GetBaseUri(XElement element)
    {
        var location = new Uri((string)element.Attribute("Location")!);
        return new Uri(location.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool? GetIsWireless(XElement element) => (string?)element.Attribute("EthLink") switch
    {
        "1" => false,
        "0" => true,
        _ => null
    };

    private static bool ContainsUuid(string? channelMap, string uuid) =>
        channelMap is not null && FindChannels(channelMap, uuid) is not null;

    private static string[]? FindChannels(string channelMap, string uuid)
    {
        foreach (var entry in channelMap.Split(';'))
        {
            if (entry.Length > uuid.Length && entry[uuid.Length] == ':' && entry.StartsWith(uuid, StringComparison.Ordinal))
            {
                return entry[(uuid.Length + 1)..].Split(',');
            }
        }

        return null;
    }

    private static SonosSatelliteRole GetRole(string? channelMap, string uuid, bool isStereoPair)
    {
        var channels = channelMap is null ? null : FindChannels(channelMap, uuid);
        if (channels is not null)
        {
            if (channels.Contains("SW"))
            {
                return SonosSatelliteRole.Subwoofer;
            }

            if (channels.Contains("LR"))
            {
                return SonosSatelliteRole.RearLeft;
            }

            if (channels.Contains("RR"))
            {
                return SonosSatelliteRole.RearRight;
            }
        }

        return isStereoPair ? SonosSatelliteRole.StereoPartner : SonosSatelliteRole.Other;
    }
}
