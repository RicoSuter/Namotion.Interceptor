using System.Globalization;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Finds Sonos households over SSDP: every speaker that answers names its household, and the speakers of one
/// household are one result. The device description of each speaker names its room and model.
/// </summary>
public sealed class SonosHouseholdDiscoverer : SubjectDiscoverer<SonosSystem, SonosDiscoveredHousehold>
{
    internal const string HouseholdHeader = "X-RINCON-HOUSEHOLD";

    /// <summary>
    /// The most bytes read from one device description. That of a speaker is about 10 KB.
    /// </summary>
    internal const int MaximumDescriptionBytes = 64 * 1024;

    /// <summary>
    /// The most device descriptions read in one scan, so that a hostile or malfunctioning answerer on the network
    /// cannot make a scan read an unbounded number of them. Further speakers are listed by their ID.
    /// </summary>
    internal const int MaximumDescriptions = 64;

    private static readonly TimeSpan DefaultDescriptionTimeout = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _descriptionTimeout;

    public SonosHouseholdDiscoverer(IHttpClientFactory httpClientFactory)
        : this(httpClientFactory, DefaultDescriptionTimeout)
    {
    }

    internal SonosHouseholdDiscoverer(IHttpClientFactory httpClientFactory, TimeSpan descriptionTimeout)
    {
        _httpClientFactory = httpClientFactory;
        _descriptionTimeout = descriptionTimeout;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<SonosDiscoveredHousehold> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responses = await scan.Ssdp.SearchAsync(SonosDiscovery.ZonePlayerSearchTarget, cancellationToken);
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

    /// <inheritdoc />
    protected override bool Matches(SonosSystem existing, SonosDiscoveredHousehold result) => IsSameHousehold(existing, result);

    /// <summary>
    /// Reads the device descriptions of the speakers in parallel, together within the description timeout, and
    /// returns the households with the room and model of each speaker whose description was read.
    /// </summary>
    internal async Task<IReadOnlyList<SonosDiscoveredHousehold>> DescribeSpeakersAsync(
        IReadOnlyList<SonosDiscoveredHousehold> households, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient(nameof(SonosHouseholdDiscoverer));
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        httpClient.MaxResponseContentBufferSize = MaximumDescriptionBytes;

        // The requests start together, so one deadline gives each of them the timeout.
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_descriptionTimeout);

        var speakerCount = 0;
        foreach (var household in households)
        {
            speakerCount += household.Speakers.Count;
        }

        var tasks = new Task<SonosDiscoveredSpeaker>[speakerCount];
        var describedCount = 0;
        var taskIndex = 0;
        foreach (var household in households)
        {
            foreach (var speaker in household.Speakers)
            {
                tasks[taskIndex++] = speaker.DescriptionLocation is not null && describedCount++ < MaximumDescriptions
                    ? DescribeSpeakerAsync(httpClient, speaker, timeoutCancellation.Token, cancellationToken)
                    : Task.FromResult(speaker);
            }
        }

        // Awaited together, so that no request still runs once the client is disposed.
        var describedSpeakers = await Task.WhenAll(tasks);

        var described = new SonosDiscoveredHousehold[households.Count];
        var offset = 0;
        for (var householdIndex = 0; householdIndex < households.Count; householdIndex++)
        {
            var household = households[householdIndex];
            var count = household.Speakers.Count;
            described[householdIndex] = new SonosDiscoveredHousehold(
                household.HouseholdId, new ArraySegment<SonosDiscoveredSpeaker>(describedSpeakers, offset, count), household.SeedHost);
            offset += count;
        }

        return described;
    }

    /// <returns>
    /// The speaker with its room and model, or the speaker as it was when its description does not answer in time,
    /// answers with an error, from another address after a redirect, with more than
    /// <see cref="MaximumDescriptionBytes"/> or with a document that cannot be parsed.
    /// </returns>
    private static async Task<SonosDiscoveredSpeaker> DescribeSpeakerAsync(
        HttpClient httpClient, SonosDiscoveredSpeaker speaker, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        try
        {
            var location = speaker.DescriptionLocation!;
            using var response = await httpClient.GetAsync(location, HttpCompletionOption.ResponseContentRead, timeoutToken);

            // A redirect could have led the request away from the speaker.
            if (!response.IsSuccessStatusCode || (response.RequestMessage?.RequestUri is { } requestUri && requestUri != location))
            {
                return speaker;
            }

            var description = DeviceDescriptionParser.Parse(await response.Content.ReadAsStringAsync(timeoutToken));
            return speaker with
            {
                RoomName = TrimToNull(description.RoomName),
                ModelName = TrimToNull(description.ModelName)
            };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever the speaker answers, the household is still listed.
            return speaker;
        }
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
            speakers.TryAdd(speakerId, new SonosDiscoveredSpeaker(speakerId, response.Location.Host, response.Location.Port)
            {
                DescriptionLocation = IsDescriptionLocation(response.Location) ? response.Location : null
            });
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

    // Only the speaker itself is asked: the host of the location is the address of the speaker, and credentials or
    // another scheme are no description of a Sonos speaker.
    private static bool IsDescriptionLocation(Uri location) =>
        location.IsAbsoluteUri &&
        string.Equals(location.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
        location.UserInfo.Length == 0;

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
