using System.Globalization;
using System.Xml;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Reads the device descriptions of discovered speakers for their room and model, bounded against hostile or
/// malfunctioning answerers on the network.
/// </summary>
internal static class SonosSpeakerDescriptions
{
    /// <summary>
    /// The most bytes read from one device description. That of a speaker is about 10 KB.
    /// </summary>
    internal const int MaximumDescriptionBytes = 64 * 1024;

    /// <summary>
    /// The most device descriptions read in one scan, so that a hostile or malfunctioning answerer on the network
    /// cannot make a scan read an unbounded number of them. Further speakers are listed by their ID.
    /// </summary>
    internal const int MaximumDescriptions = 64;

    /// <summary>
    /// The most characters kept of a room or model name.
    /// </summary>
    internal const int MaximumNameLength = 64;

    /// <summary>
    /// How long the device descriptions of one scan may take, all requests together.
    /// </summary>
    internal static readonly TimeSpan DescriptionTimeout = TimeSpan.FromSeconds(2);

    private const string DescriptionPath = "/xml/device_description.xml";

    /// <summary>
    /// Reads the device descriptions of the speakers in parallel, together within the description timeout, and
    /// returns the households with the room and model of each speaker whose description was read.
    /// </summary>
    /// <param name="households">The households to describe the speakers of.</param>
    /// <param name="handler">The handler to send the requests through, not disposed; null for a new one.</param>
    /// <param name="timeProvider">Runs the deadline of the requests.</param>
    /// <param name="cancellationToken">Cancels every request; its cancellation propagates.</param>
    internal static async Task<IReadOnlyList<SonosDiscoveredHousehold>> DescribeAsync(
        IReadOnlyList<SonosDiscoveredHousehold> households, HttpMessageHandler? handler, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var httpClient = handler is null
            ? new HttpClient(CreateHandler(), disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        httpClient.MaxResponseContentBufferSize = MaximumDescriptionBytes;

        // The requests start together, so one deadline gives each of them the timeout.
        using var deadline = new CancellationTokenSource(DescriptionTimeout, timeProvider);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        var speakerCount = 0;
        foreach (var household in households)
        {
            speakerCount += household.Speakers.Count;
        }

        var tasks = new Task<SonosDiscoveredSpeaker>[speakerCount];
        var taskIndex = 0;
        foreach (var household in households)
        {
            foreach (var speaker in household.Speakers)
            {
                tasks[taskIndex] = taskIndex < MaximumDescriptions
                    ? DescribeSpeakerAsync(httpClient, speaker, timeoutCancellation.Token, cancellationToken)
                    : Task.FromResult(speaker);
                taskIndex++;
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

    /// <summary>
    /// Creates the handler for the description requests: no redirects and no proxy, since either could send the
    /// request to another host than the one the answer named, so a redirect fails the request.
    /// </summary>
    private static SocketsHttpHandler CreateHandler() =>
        new() { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = DescriptionTimeout };

    /// <summary>
    /// Reads the description of a speaker the way a connection reads it, plain HTTP at the fixed path on the host
    /// and port of its answer, ignoring the path and query the answer advertised.
    /// </summary>
    /// <returns>
    /// The speaker with its room and model, or the speaker as it was when its description does not answer in time,
    /// answers with an error or a redirect, with more than <see cref="MaximumDescriptionBytes"/>, in an unknown
    /// character set or with a document that cannot be parsed.
    /// </returns>
    private static async Task<SonosDiscoveredSpeaker> DescribeSpeakerAsync(
        HttpClient httpClient, SonosDiscoveredSpeaker speaker, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(
                string.Create(CultureInfo.InvariantCulture, $"{Uri.UriSchemeHttp}://{speaker.HostAddress}:{speaker.Port}{DescriptionPath}"),
                UriKind.Absolute,
                out var location))
        {
            return speaker;
        }

        try
        {
            using var response = await httpClient.GetAsync(location, HttpCompletionOption.ResponseContentRead, timeoutToken);
            if (!response.IsSuccessStatusCode)
            {
                return speaker;
            }

            var description = DeviceDescriptionParser.Parse(await response.Content.ReadAsStringAsync(timeoutToken));
            return speaker with
            {
                RoomName = ToName(description.RoomName),
                ModelName = ToName(description.ModelName)
            };
        }
        catch (Exception exception) when (IsDescriptionFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            return speaker;
        }
    }

    // An InvalidOperationException is what reading a body in an unknown character set throws.
    private static bool IsDescriptionFailure(Exception exception) =>
        exception is HttpRequestException or XmlException or OperationCanceledException or InvalidOperationException;

    private static string? ToName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var name = value.Trim();
        if (name.Length <= MaximumNameLength)
        {
            return name;
        }

        // A surrogate pair is kept whole or dropped, never cut in half.
        var length = char.IsHighSurrogate(name[MaximumNameLength - 1]) ? MaximumNameLength - 1 : MaximumNameLength;
        return name[..length];
    }
}
