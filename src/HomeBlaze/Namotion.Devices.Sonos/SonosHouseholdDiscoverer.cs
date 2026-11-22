using System.Globalization;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Devices.Sonos.Client;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Finds Sonos households over SSDP: every speaker that answers names its household, and the speakers of one
/// household are one result.
/// </summary>
public sealed class SonosHouseholdDiscoverer : SubjectDiscoverer<SonosSystem, SonosDiscoveredHousehold>
{
    internal const string HouseholdHeader = "X-RINCON-HOUSEHOLD";

    /// <inheritdoc />
    protected override async IAsyncEnumerable<SonosDiscoveredHousehold> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responses = await scan.Ssdp.SearchAsync(SonosDiscovery.ZonePlayerSearchTarget, cancellationToken);
        foreach (var household in ToHouseholds(responses))
        {
            yield return household;
        }
    }

    /// <inheritdoc />
    protected override bool Matches(SonosSystem existing, SonosDiscoveredHousehold result) => IsSameHousehold(existing, result);

    internal static IReadOnlyList<SonosDiscoveredHousehold> ToHouseholds(IReadOnlyList<SsdpResponse> responses)
    {
        var speakersByHousehold = new Dictionary<string, Dictionary<string, SonosDiscoveredSpeaker>>(StringComparer.Ordinal);
        foreach (var response in responses)
        {
            var speakerId = SonosDiscovery.GetSpeakerId(response.UniqueServiceName);
            if (speakerId is null ||
                !response.Headers.TryGetValue(HouseholdHeader, out var householdId) ||
                string.IsNullOrWhiteSpace(householdId))
            {
                continue;
            }

            householdId = householdId.Trim();
            if (!speakersByHousehold.TryGetValue(householdId, out var speakers))
            {
                speakers = new Dictionary<string, SonosDiscoveredSpeaker>(StringComparer.Ordinal);
                speakersByHousehold.Add(householdId, speakers);
            }

            // A speaker that answers on several adapters is kept once.
            speakers.TryAdd(speakerId, new SonosDiscoveredSpeaker(speakerId, response.Location.Host, response.Location.Port));
        }

        var households = new List<SonosDiscoveredHousehold>(speakersByHousehold.Count);
        foreach (var (householdId, speakers) in speakersByHousehold.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // The speaker with the lowest ID seeds, so the same household gives the same seed in every scan.
            var orderedSpeakers = speakers.Values.OrderBy(speaker => speaker.Id, StringComparer.Ordinal).ToArray();
            households.Add(new SonosDiscoveredHousehold(householdId, orderedSpeakers, ToSeedHost(orderedSpeakers[0])));
        }

        return households;
    }

    internal static bool IsSameHousehold(SonosSystem existing, SonosDiscoveredHousehold result)
    {
        // The dictionaries are replaced, never mutated, so each one is read once and enumerated safely.
        var players = existing.Players;
        foreach (var speaker in result.Speakers)
        {
            if (players.ContainsKey(speaker.Id))
            {
                return true;
            }
        }

        foreach (var player in players.Values)
        {
            var satellites = player.Satellites;
            foreach (var speaker in result.Speakers)
            {
                if (satellites.ContainsKey(speaker.Id))
                {
                    return true;
                }
            }
        }

        // A system that has not connected yet knows no speakers, only its seed.
        var seedHostAddress = GetHostAddress(existing.SeedHost);
        if (seedHostAddress is not null)
        {
            foreach (var speaker in result.Speakers)
            {
                if (string.Equals(speaker.HostAddress, seedHostAddress, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ToSeedHost(SonosDiscoveredSpeaker speaker) =>
        speaker.Port == SonosValues.DevicePort
            ? speaker.HostAddress
            : string.Create(CultureInfo.InvariantCulture, $"{speaker.HostAddress}:{speaker.Port}");

    private static string? GetHostAddress(string? seedHost)
    {
        if (string.IsNullOrWhiteSpace(seedHost))
        {
            return null;
        }

        try
        {
            return SonosDiscovery.CreateDeviceUri(seedHost).Host;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
