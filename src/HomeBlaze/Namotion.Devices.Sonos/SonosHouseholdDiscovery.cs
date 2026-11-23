using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Devices.Sonos.Client;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Finds Sonos households over SSDP: every speaker that answers names its household, and the speakers of one
/// household are one result. The device description of each speaker names its room and model.
/// </summary>
public sealed class SonosHouseholdDiscovery : SubjectDiscovery<SonosSystem, SonosDiscoveredHousehold>
{
    internal const string HouseholdHeader = "X-RINCON-HOUSEHOLD";

    private readonly ISsdpSearch _ssdpSearch;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeProvider _timeProvider;

    public SonosHouseholdDiscovery(ISsdpSearch ssdpSearch)
        : this(ssdpSearch, handler: null, TimeProvider.System)
    {
    }

    /// <param name="ssdpSearch">The SSDP search to find the speakers with.</param>
    /// <param name="handler">The handler to send the description requests through, not disposed; null for a new one per scan.</param>
    /// <param name="timeProvider">Runs the deadline of the description requests.</param>
    internal SonosHouseholdDiscovery(ISsdpSearch ssdpSearch, HttpMessageHandler? handler, TimeProvider timeProvider)
    {
        _ssdpSearch = ssdpSearch;
        _handler = handler;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<SonosDiscoveredHousehold> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responses = await _ssdpSearch.SearchAsync(SonosDiscovery.ZonePlayerSearchTarget, cancellationToken);
        var households = ToHouseholds(responses);
        if (households.Count == 0)
        {
            yield break;
        }

        foreach (var household in await DescribeSpeakersAsync(households, cancellationToken))
        {
            yield return household;
        }
    }

    /// <summary>
    /// Returns the households with the room and model of each speaker whose device description was read.
    /// </summary>
    internal Task<IReadOnlyList<SonosDiscoveredHousehold>> DescribeSpeakersAsync(
        IReadOnlyList<SonosDiscoveredHousehold> households, CancellationToken cancellationToken) =>
        SonosSpeakerDescriptions.DescribeAsync(households, _handler, _timeProvider, cancellationToken);

    internal static IReadOnlyList<SonosDiscoveredHousehold> ToHouseholds(IReadOnlyList<SsdpResponse> responses)
    {
        var speakersByHousehold = new Dictionary<string, Dictionary<string, SonosDiscoveredSpeaker>>(StringComparer.Ordinal);
        foreach (var response in responses)
        {
            var speakerId = SonosDiscovery.GetSpeakerId(response.UniqueServiceName);
            if (speakerId is null ||
                !IsSpeakerLocation(response.Location) ||
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

    // The host and port of the location are where the speaker is asked, as seed and for its description; an absolute
    // location such as urn:x has neither. A speaker announces an IP address, so a host name, such as one of the
    // internet, and an address of this host, which anything on the network can advertise, are not asked.
    private static bool IsSpeakerLocation(Uri location) =>
        location.IsAbsoluteUri &&
        string.Equals(location.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
        IsNetworkAddress(location) &&
        location.Port is >= 1 and <= 65535;

    private static bool IsNetworkAddress(Uri location)
    {
        if (location.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            !IPAddress.TryParse(location.DnsSafeHost, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
    }

    private static string ToSeedHost(SonosDiscoveredSpeaker speaker) =>
        speaker.Port == SonosValues.DevicePort
            ? speaker.HostAddress
            : string.Create(CultureInfo.InvariantCulture, $"{speaker.HostAddress}:{speaker.Port}");
}
