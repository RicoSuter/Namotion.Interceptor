using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// The connection to one Sonos unit. Reads return our records; commands throw on a UPnP fault.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "Holds every action of one unit; splitting it by service would move the coupling, not remove it.")]
internal sealed class SonosConnection
{
    private const string UuidPrefix = "RINCON";

    private static readonly (string Name, string Value) Instance = ("InstanceID", "0");
    private static readonly (string Name, string Value) MasterChannel = ("Channel", "Master");

    // The audio clip websocket is not covered by the HttpClient timeout.
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NotificationCloseTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly SonosSoapClient _soapClient;

    // The reads that answer with a UPnP fault, so each is reported once when it starts faulting. Concurrent because
    // a command refresh can poll the same player as the reconciliation.
    private readonly FailureTracker _faultingReads = new();

    // The pages of the last favorites read, replaced as a whole.
    private FavoritesPage[] _favoritesPages = [];

    // Read from the unit when the connection was created without it; racing readers store the same value.
    private volatile string? _uuid;

    /// <param name="baseUri">The base URI of the unit.</param>
    /// <param name="uuid">The RINCON id of the unit, null while unknown.</param>
    /// <param name="httpClient">The client for all requests, borrowed and not disposed.</param>
    /// <param name="logger">The logger.</param>
    internal SonosConnection(Uri baseUri, string? uuid, HttpClient httpClient, ILogger? logger = null)
    {
        BaseUri = baseUri;
        _httpClient = httpClient;
        _logger = logger ?? NullLogger.Instance;
        _soapClient = new SonosSoapClient(httpClient, baseUri);
        _uuid = uuid;
    }

    internal Uri BaseUri { get; }

    internal async Task<SonosDeviceDescription> ReadDescriptionAsync(CancellationToken cancellationToken)
    {
        var xml = await _httpClient.GetStringAsync(new Uri(BaseUri, "/xml/device_description.xml"), cancellationToken);
        return DeviceDescriptionParser.Parse(xml);
    }

    /// <summary>
    /// Reads the zone info; null when the speaker answers with a UPnP fault, which is added to
    /// <paramref name="newFaults"/> when it is new.
    /// </summary>
    internal async Task<SonosZoneInfo?> ReadZoneInfoAsync(List<SonosReadFault> newFaults, CancellationToken cancellationToken)
    {
        var zoneInfo = await ReadOrDefaultAsync(SonosService.DeviceProperties, "GetZoneInfo", [], newFaults, cancellationToken);
        return zoneInfo is null
            ? null
            : new SonosZoneInfo(
                zoneInfo.GetString("SerialNumber"),
                zoneInfo.GetString("MACAddress"),
                zoneInfo.GetString("HardwareVersion"),
                zoneInfo.GetString("DisplaySoftwareVersion"));
    }

    internal async Task<SonosTopology> ReadTopologyAsync(CancellationToken cancellationToken) =>
        ZoneGroupStateParser.Parse(await ReadZoneGroupStateAsync(cancellationToken));

    /// <summary>
    /// Reads the raw ZoneGroupState XML, which <see cref="ZoneGroupStateParser"/> parses.
    /// </summary>
    internal async Task<string> ReadZoneGroupStateAsync(CancellationToken cancellationToken)
    {
        var response = await _soapClient.InvokeAsync(SonosService.ZoneGroupTopology, "GetZoneGroupState", [], cancellationToken);
        return response.GetString("ZoneGroupState") ?? string.Empty;
    }

    internal async Task<IReadOnlyList<SonosFavorite>> ReadFavoritesAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var previousPages = _favoritesPages;
        var pages = new List<FavoritesPage>();
        var favorites = new List<SonosFavorite>();
        var startingIndex = 0;
        while (true)
        {
            var response = await BrowseFavoritesAsync(startingIndex, pageSize, cancellationToken);
            var result = response.GetString("Result");
            var numberReturned = response.GetInt32("NumberReturned") ?? 0;

            // Favorites rarely change, so a page with the same result as last time reuses its records.
            var pageIndex = pages.Count;
            var page = pageIndex < previousPages.Length && previousPages[pageIndex].Result == result
                ? previousPages[pageIndex]
                : new FavoritesPage(result, FavoritesParser.Parse(result, BaseUri));
            pages.Add(page);
            favorites.AddRange(page.Favorites);

            // Counted by the items returned, not the favorites parsed, which skip shortcuts.
            startingIndex += numberReturned;
            if (numberReturned <= 0 || startingIndex >= (response.GetInt32("TotalMatches") ?? 0))
            {
                _favoritesPages = [.. pages];
                return favorites;
            }
        }
    }

    private Task<SonosSoapResponse> BrowseFavoritesAsync(int startingIndex, int count, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.ContentDirectory,
            "Browse",
            [
                ("ObjectID", "FV:2"),
                ("BrowseFlag", "BrowseDirectChildren"),
                ("Filter", "*"),
                ("StartingIndex", Format(startingIndex)),
                ("RequestedCount", Format(count)),
                ("SortCriteria", string.Empty)
            ],
            cancellationToken);

    private sealed record FavoritesPage(string? Result, IReadOnlyList<SonosFavorite> Favorites);

    /// <summary>
    /// Reads one player. Requests go one after another: a poll is a dozen small calls, and players are polled in
    /// parallel already. A read the speaker answers with a UPnP fault leaves its values unknown and is added to
    /// <paramref name="newFaults"/> when it was not faulting before; a transport failure throws.
    /// </summary>
    internal async Task<SonosPlayerReading> ReadPlayerAsync(bool isHomeTheater, List<SonosReadFault> newFaults, CancellationToken cancellationToken)
    {
        var transport = await ReadOrDefaultAsync(SonosService.AvTransport, "GetTransportInfo", [Instance], newFaults, cancellationToken);
        var settings = await ReadOrDefaultAsync(SonosService.AvTransport, "GetTransportSettings", [Instance], newFaults, cancellationToken);
        var media = await ReadOrDefaultAsync(SonosService.AvTransport, "GetMediaInfo", [Instance], newFaults, cancellationToken);
        var position = await ReadOrDefaultAsync(SonosService.AvTransport, "GetPositionInfo", [Instance], newFaults, cancellationToken);
        var sleepTimer = await ReadOrDefaultAsync(SonosService.AvTransport, "GetRemainingSleepTimerDuration", [Instance], newFaults, cancellationToken);

        var volume = await ReadOrDefaultAsync(SonosService.RenderingControl, "GetVolume", [Instance, MasterChannel], newFaults, cancellationToken);
        var mute = await ReadOrDefaultAsync(SonosService.RenderingControl, "GetMute", [Instance, MasterChannel], newFaults, cancellationToken);
        var bass = await ReadOrDefaultAsync(SonosService.RenderingControl, "GetBass", [Instance], newFaults, cancellationToken);
        var treble = await ReadOrDefaultAsync(SonosService.RenderingControl, "GetTreble", [Instance], newFaults, cancellationToken);
        var loudness = await ReadOrDefaultAsync(SonosService.RenderingControl, "GetLoudness", [Instance, MasterChannel], newFaults, cancellationToken);

        bool? nightMode = null;
        bool? speechEnhancement = null;
        if (isHomeTheater)
        {
            nightMode = await GetEqualizerAsync("NightMode", newFaults, cancellationToken);
            speechEnhancement = await GetEqualizerAsync("DialogLevel", newFaults, cancellationToken);
        }

        return new SonosPlayerReading(
            CreateAvTransportChange(transport, settings, media, position),
            SonosValues.ParseDuration(position?.GetString("RelTime")),
            SonosValues.ParseDuration(sleepTimer?.GetString("RemainingSleepTimerDuration")),
            new RenderingControlChange(
                volume?.GetInt32("CurrentVolume"),
                mute?.GetBoolean("CurrentMute"),
                bass?.GetInt32("CurrentBass"),
                treble?.GetInt32("CurrentTreble"),
                loudness?.GetBoolean("CurrentLoudness"),
                nightMode,
                speechEnhancement),
            HasPosition: position is not null,
            HasSleepTimer: sleepTimer is not null);
    }

    private static AvTransportChange CreateAvTransportChange(
        SonosSoapResponse? transport, SonosSoapResponse? settings, SonosSoapResponse? media, SonosSoapResponse? position) =>
        new(
            transport?.GetString("CurrentTransportState"),
            settings?.GetString("PlayMode"),
            media?.GetString("CurrentURI"),
            position?.GetString("TrackURI"),
            position?.GetString("TrackDuration"),
            position?.GetString("TrackMetaData"),
            media?.GetString("CurrentURIMetaData"));

    /// <summary>
    /// Reads the group volume and mute from a coordinator, with the same fault handling as <see cref="ReadPlayerAsync"/>.
    /// </summary>
    internal async Task<GroupRenderingControlChange> ReadGroupAsync(List<SonosReadFault> newFaults, CancellationToken cancellationToken)
    {
        var volume = await ReadOrDefaultAsync(SonosService.GroupRenderingControl, "GetGroupVolume", [Instance], newFaults, cancellationToken);
        var mute = await ReadOrDefaultAsync(SonosService.GroupRenderingControl, "GetGroupMute", [Instance], newFaults, cancellationToken);
        return new GroupRenderingControlChange(volume?.GetInt32("CurrentVolume"), mute?.GetBoolean("CurrentMute"));
    }

    private async Task<bool?> GetEqualizerAsync(string type, List<SonosReadFault> newFaults, CancellationToken cancellationToken)
    {
        var response = await ReadOrDefaultAsync(
            $"GetEQ {type}",
            _soapClient.InvokeAsync(SonosService.RenderingControl, "GetEQ", [Instance, ("EQType", type)], cancellationToken),
            newFaults);
        return response?.GetInt32("CurrentValue") is { } level ? SonosValues.IsLevelOn(level) : null;
    }

    private Task<SonosSoapResponse?> ReadOrDefaultAsync(
        SonosService service,
        string action,
        ReadOnlySpan<(string Name, string Value)> arguments,
        List<SonosReadFault> newFaults,
        CancellationToken cancellationToken) =>
        ReadOrDefaultAsync(action, _soapClient.InvokeAsync(service, action, arguments, cancellationToken), newFaults);

    // A UPnP fault means the speaker answered but cannot report this value now, for example an action a model or a
    // grouped member does not support; the other reads still count.
    private async Task<SonosSoapResponse?> ReadOrDefaultAsync(string read, Task<SonosSoapResponse> response, List<SonosReadFault> newFaults)
    {
        try
        {
            var result = await response;
            _faultingReads.ReportSuccess(read);
            return result;
        }
        catch (SonosFaultException exception)
        {
            if (_faultingReads.ReportFailure(read))
            {
                newFaults.Add(new SonosReadFault(read, exception));
            }

            return null;
        }
    }

    internal Task PlayAsync(CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(SonosService.AvTransport, "Play", [Instance, ("Speed", "1")], cancellationToken);

    internal Task PauseAsync(CancellationToken cancellationToken) => InvokeTransportAsync("Pause", cancellationToken);

    internal Task StopAsync(CancellationToken cancellationToken) => InvokeTransportAsync("Stop", cancellationToken);

    internal Task NextAsync(CancellationToken cancellationToken) => InvokeTransportAsync("Next", cancellationToken);

    internal Task PreviousAsync(CancellationToken cancellationToken) => InvokeTransportAsync("Previous", cancellationToken);

    /// <summary>
    /// Pauses a playing or starting player and plays any other; does nothing when the speaker reports no state.
    /// </summary>
    internal async Task TogglePlaybackAsync(CancellationToken cancellationToken)
    {
        var transport = await _soapClient.InvokeAsync(SonosService.AvTransport, "GetTransportInfo", [Instance], cancellationToken);
        var state = transport.GetString("CurrentTransportState");
        if (string.IsNullOrEmpty(state))
        {
            return;
        }

        if (state is "PLAYING" or "TRANSITIONING")
        {
            await PauseAsync(cancellationToken);
        }
        else
        {
            await PlayAsync(cancellationToken);
        }
    }

    internal Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        return _soapClient.InvokeAsync(
            SonosService.AvTransport, "Seek", [Instance, ("Unit", "REL_TIME"), ("Target", SonosValues.FormatDuration(position))], cancellationToken);
    }

    internal Task SetVolumeAsync(int volume, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.RenderingControl, "SetVolume", [Instance, MasterChannel, ("DesiredVolume", Format(volume))], cancellationToken);

    internal Task ChangeVolumeAsync(int adjustment, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.RenderingControl, "SetRelativeVolume", [Instance, MasterChannel, ("Adjustment", Format(adjustment))], cancellationToken);

    internal Task RampVolumeAsync(int volume, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.RenderingControl,
            "RampToVolume",
            [
                Instance,
                MasterChannel,
                ("RampType", "SLEEP_TIMER_RAMP_TYPE"),
                ("DesiredVolume", Format(volume)),
                ("ResetVolumeAfter", Format(false)),
                ("ProgramURI", string.Empty)
            ],
            cancellationToken);

    internal Task SetMuteAsync(bool mute, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.RenderingControl, "SetMute", [Instance, MasterChannel, ("DesiredMute", Format(mute))], cancellationToken);

    internal Task SetTransportUriAsync(string uri, string metadata, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.AvTransport, "SetAVTransportURI", [Instance, ("CurrentURI", uri), ("CurrentURIMetaData", metadata)], cancellationToken);

    internal async Task PlayFromQueueAsync(string uri, string metadata, CancellationToken cancellationToken)
    {
        await InvokeTransportAsync("RemoveAllTracksFromQueue", cancellationToken);
        await _soapClient.InvokeAsync(
            SonosService.AvTransport,
            "AddURIToQueue",
            [
                Instance,
                ("EnqueuedURI", uri),
                ("EnqueuedURIMetaData", metadata),
                ("DesiredFirstTrackNumberEnqueued", "0"),
                ("EnqueueAsNext", Format(false))
            ],
            cancellationToken);
        await SetTransportUriAsync($"x-rincon-queue:{await GetUuidAsync(cancellationToken)}#0", string.Empty, cancellationToken);
        await PlayAsync(cancellationToken);
    }

    /// <summary>
    /// Queues an audio clip over the speaker's websocket. The speaker's answer is not read, so a rejected clip is
    /// not reported.
    /// </summary>
    internal async Task PlayNotificationAsync(Uri soundUri, int volume, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(volume, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, 100);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NotificationTimeout);
        var uuid = await GetUuidAsync(timeout.Token);
        await SonosAudioClipClient.QueueAsync(BaseUri.Host, uuid, soundUri, volume, NotificationCloseTimeout, _logger, timeout.Token);
    }

    internal async Task SwitchToTvAsync(CancellationToken cancellationToken) =>
        await SetTransportUriAsync($"x-sonos-htastream:{await GetUuidAsync(cancellationToken)}:spdif", string.Empty, cancellationToken);

    internal async Task SwitchToLineInAsync(CancellationToken cancellationToken) =>
        await SetTransportUriAsync($"x-rincon-stream:{await GetUuidAsync(cancellationToken)}", string.Empty, cancellationToken);

    internal Task SetPlayModeAsync(string playMode, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(SonosService.AvTransport, "SetPlayMode", [Instance, ("NewPlayMode", playMode)], cancellationToken);

    /// <summary>
    /// Sets the sleep timer, at most <see cref="SonosValues.MaximumSleepTimer"/>; zero cancels it.
    /// </summary>
    internal Task SetSleepTimerAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, SonosValues.MaximumSleepTimer);
        return _soapClient.InvokeAsync(
            SonosService.AvTransport,
            "ConfigureSleepTimer",
            [Instance, ("NewSleepTimerDuration", duration == TimeSpan.Zero ? string.Empty : SonosValues.FormatDuration(duration))],
            cancellationToken);
    }

    internal Task SetBassAsync(int bass, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(SonosService.RenderingControl, "SetBass", [Instance, ("DesiredBass", Format(bass))], cancellationToken);

    internal Task SetTrebleAsync(int treble, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(SonosService.RenderingControl, "SetTreble", [Instance, ("DesiredTreble", Format(treble))], cancellationToken);

    internal Task SetLoudnessAsync(bool loudness, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.RenderingControl, "SetLoudness", [Instance, MasterChannel, ("DesiredLoudness", Format(loudness))], cancellationToken);

    internal Task SetEqualizerAsync(string type, bool enabled, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.RenderingControl, "SetEQ", [Instance, ("EQType", type), ("DesiredValue", enabled ? "1" : "0")], cancellationToken);

    internal Task JoinAsync(string coordinatorUuid, CancellationToken cancellationToken) =>
        SetTransportUriAsync($"x-rincon:{coordinatorUuid}", string.Empty, cancellationToken);

    internal Task LeaveGroupAsync(CancellationToken cancellationToken) =>
        InvokeTransportAsync("BecomeCoordinatorOfStandaloneGroup", cancellationToken);

    // Both group volume commands scale the members from the last snapshot, so a fresh one keeps the current volume
    // ratio between them; without it, a member changed in the Sonos app since would jump back.

    internal async Task SetGroupVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        await SnapshotGroupVolumeAsync(cancellationToken);
        await _soapClient.InvokeAsync(
            SonosService.GroupRenderingControl, "SetGroupVolume", [Instance, ("DesiredVolume", Format(volume))], cancellationToken);
    }

    internal async Task ChangeGroupVolumeAsync(int adjustment, CancellationToken cancellationToken)
    {
        await SnapshotGroupVolumeAsync(cancellationToken);
        await _soapClient.InvokeAsync(
            SonosService.GroupRenderingControl, "SetRelativeGroupVolume", [Instance, ("Adjustment", Format(adjustment))], cancellationToken);
    }

    internal Task SetGroupMuteAsync(bool mute, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(
            SonosService.GroupRenderingControl, "SetGroupMute", [Instance, ("DesiredMute", Format(mute))], cancellationToken);

    private Task SnapshotGroupVolumeAsync(CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(SonosService.GroupRenderingControl, "SnapshotGroupVolume", [Instance], cancellationToken);

    private Task InvokeTransportAsync(string action, CancellationToken cancellationToken) =>
        _soapClient.InvokeAsync(SonosService.AvTransport, action, [Instance], cancellationToken);

    /// <summary>
    /// Returns the RINCON id of the unit, read from its zone info when the connection was created without one.
    /// </summary>
    private async ValueTask<string> GetUuidAsync(CancellationToken cancellationToken)
    {
        if (_uuid is { } uuid && uuid.StartsWith(UuidPrefix, StringComparison.Ordinal))
        {
            return uuid;
        }

        var zoneInfo = await _soapClient.InvokeAsync(SonosService.DeviceProperties, "GetZoneInfo", [], cancellationToken);
        var macAddress = zoneInfo.GetString("MACAddress")
            ?? throw new InvalidOperationException($"The Sonos speaker {BaseUri} did not report its MAC address.");

        // A RINCON id is the MAC address followed by a zero and the device port.
        uuid = $"{UuidPrefix}_{macAddress.Replace(":", string.Empty, StringComparison.Ordinal)}0{BaseUri.Port.ToString(CultureInfo.InvariantCulture)}";
        _uuid = uuid;
        return uuid;
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(bool value) => value ? "true" : "false";
}
