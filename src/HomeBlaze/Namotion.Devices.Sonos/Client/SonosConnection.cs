using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Parsing;
using Sonos.Base;
using Sonos.Base.Services;

// Our own SonosDevice subject lives in an enclosing namespace and would win over the using directive.
using SonosBaseDevice = Sonos.Base.SonosDevice;

namespace Namotion.Devices.Sonos.Client;

internal sealed record SonosZoneInfo(string? SerialNumber, string? MacAddress, string? HardwareVersion, string? DisplayVersion);

/// <summary>
/// The SOAP connection to one Sonos unit. Reads return our records; commands throw on a SOAP fault.
/// </summary>
internal sealed class SonosConnection : IDisposable
{
    private const int InstanceId = 0;
    private const string MasterChannel = "Master";

    // The audio clip websocket is not covered by the HttpClient timeout.
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NotificationCloseTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly SonosDeviceOptions _deviceOptions;
    private readonly SonosBaseDevice _device;

    // The reads that answer with a UPnP fault, so each is reported once when it starts faulting. Concurrent because
    // a command refresh can poll the same player as the reconciliation.
    private readonly FailureTracker _faultingReads = new();

    // The pages of the last favorites read, replaced as a whole.
    private FavoritesPage[] _favoritesPages = [];

    /// <param name="baseUri">The base URI of the unit.</param>
    /// <param name="uuid">The RINCON id of the unit, null while unknown.</param>
    /// <param name="httpClient">The client for all requests, borrowed and not disposed.</param>
    /// <param name="logger">The logger.</param>
    internal SonosConnection(Uri baseUri, string? uuid, HttpClient httpClient, ILogger? logger = null)
    {
        BaseUri = baseUri;
        _httpClient = httpClient;
        _logger = logger ?? NullLogger.Instance;
        _deviceOptions = new SonosDeviceOptions(baseUri, new SonosClientProvider(httpClient), uuid);
        _device = new SonosBaseDevice(_deviceOptions);
    }

    internal Uri BaseUri { get; }

    private AVTransportService AvTransport => _device.AVTransportService;

    private RenderingControlService RenderingControl => _device.RenderingControlService;

    private GroupRenderingControlService GroupRenderingControl => _device.GroupRenderingControlService;

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
        var zoneInfo = await ReadOrDefaultAsync("GetZoneInfo", () => _device.DevicePropertiesService.GetZoneInfo(cancellationToken), newFaults);
        return zoneInfo is null
            ? null
            : new SonosZoneInfo(zoneInfo.SerialNumber, zoneInfo.MACAddress, zoneInfo.HardwareVersion, zoneInfo.DisplaySoftwareVersion);
    }

    internal async Task<SonosTopology> ReadTopologyAsync(CancellationToken cancellationToken) =>
        ZoneGroupStateParser.Parse(await ReadZoneGroupStateAsync(cancellationToken));

    /// <summary>
    /// Reads the raw ZoneGroupState XML, which <see cref="ZoneGroupStateParser"/> parses.
    /// </summary>
    internal async Task<string> ReadZoneGroupStateAsync(CancellationToken cancellationToken)
    {
        var response = await _device.ZoneGroupTopologyService.GetZoneGroupState(cancellationToken);
        return response.ZoneGroupState;
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
            var response = await _device.ContentDirectoryService.Browse("FV:2", StartingIndex: startingIndex, Count: pageSize, cancellationToken: cancellationToken);

            // Favorites rarely change, so a page with the same result as last time reuses its records.
            var pageIndex = pages.Count;
            var page = pageIndex < previousPages.Length && previousPages[pageIndex].Result == response.Result
                ? previousPages[pageIndex]
                : new FavoritesPage(response.Result, FavoritesParser.Parse(response.Result, BaseUri));
            pages.Add(page);
            favorites.AddRange(page.Favorites);

            // Counted by the items returned, not the favorites parsed, which skip shortcuts.
            startingIndex += response.NumberReturned;
            if (response.NumberReturned <= 0 || startingIndex >= response.TotalMatches)
            {
                _favoritesPages = [.. pages];
                return favorites;
            }
        }
    }

    private sealed record FavoritesPage(string? Result, IReadOnlyList<SonosFavorite> Favorites);

    /// <summary>
    /// Reads one player. Requests go one after another: a poll is a dozen small calls, and players are polled in
    /// parallel already. A read the speaker answers with a UPnP fault leaves its values unknown and is added to
    /// <paramref name="newFaults"/> when it was not faulting before; a transport failure throws.
    /// </summary>
    internal async Task<SonosPlayerReading> ReadPlayerAsync(bool isHomeTheater, List<SonosReadFault> newFaults, CancellationToken cancellationToken)
    {
        var transport = await ReadOrDefaultAsync("GetTransportInfo", () => AvTransport.GetTransportInfo(cancellationToken), newFaults);
        var settings = await ReadOrDefaultAsync("GetTransportSettings", () => AvTransport.GetTransportSettings(cancellationToken), newFaults);
        var media = await ReadOrDefaultAsync("GetMediaInfo", () => AvTransport.GetMediaInfo(cancellationToken), newFaults);
        var position = await ReadOrDefaultAsync("GetPositionInfo", () => AvTransport.GetPositionInfo(cancellationToken), newFaults);
        var sleepTimer = await ReadOrDefaultAsync("GetRemainingSleepTimerDuration", () => AvTransport.GetRemainingSleepTimerDuration(cancellationToken), newFaults);

        var volume = await ReadOrDefaultAsync("GetVolume", () => RenderingControl.GetVolume(new RenderingControlService.GetVolumeRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken), newFaults);
        var mute = await ReadOrDefaultAsync("GetMute", () => RenderingControl.GetMute(new RenderingControlService.GetMuteRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken), newFaults);
        var bass = await ReadOrDefaultAsync("GetBass", () => RenderingControl.GetBass(cancellationToken), newFaults);
        var treble = await ReadOrDefaultAsync("GetTreble", () => RenderingControl.GetTreble(cancellationToken), newFaults);
        var loudness = await ReadOrDefaultAsync("GetLoudness", () => RenderingControl.GetLoudness(new RenderingControlService.GetLoudnessRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken), newFaults);

        bool? nightMode = null;
        bool? speechEnhancement = null;
        if (isHomeTheater)
        {
            nightMode = await GetEqualizerAsync("NightMode", "GetEQ NightMode", newFaults, cancellationToken);
            speechEnhancement = await GetEqualizerAsync("DialogLevel", "GetEQ DialogLevel", newFaults, cancellationToken);
        }

        return new SonosPlayerReading(
            new AvTransportChange(
                transport?.CurrentTransportState,
                settings?.PlayMode,
                media?.CurrentURI,
                position?.TrackURI,
                position?.TrackDuration,
                position?.TrackMetaData,
                media?.CurrentURIMetaData),
            SonosValues.ParseDuration(position?.RelTime),
            SonosValues.ParseDuration(sleepTimer?.RemainingSleepTimerDuration),
            new RenderingControlChange(
                volume?.CurrentVolume,
                mute?.CurrentMute,
                bass?.CurrentBass,
                treble?.CurrentTreble,
                loudness?.CurrentLoudness,
                nightMode,
                speechEnhancement),
            HasPosition: position is not null,
            HasSleepTimer: sleepTimer is not null);
    }

    /// <summary>
    /// Reads the group volume and mute from a coordinator, with the same fault handling as <see cref="ReadPlayerAsync"/>.
    /// </summary>
    internal async Task<GroupRenderingControlChange> ReadGroupAsync(List<SonosReadFault> newFaults, CancellationToken cancellationToken)
    {
        var volume = await ReadOrDefaultAsync("GetGroupVolume", () => GroupRenderingControl.GetGroupVolume(cancellationToken), newFaults);
        var mute = await ReadOrDefaultAsync("GetGroupMute", () => GroupRenderingControl.GetGroupMute(cancellationToken), newFaults);
        return new GroupRenderingControlChange(volume?.CurrentVolume, mute?.CurrentMute);
    }

    // A UPnP fault means the speaker answered but cannot report this value now, for example an action a model or a
    // grouped member does not support; the other reads still count.
    private async Task<T?> ReadOrDefaultAsync<T>(string action, Func<Task<T>> read, List<SonosReadFault> newFaults)
        where T : class
    {
        try
        {
            var result = await read();
            _faultingReads.ReportSuccess(action);
            return result;
        }
        catch (SonosServiceException exception)
        {
            if (_faultingReads.ReportFailure(action))
            {
                newFaults.Add(new SonosReadFault(action, exception));
            }

            return null;
        }
    }

    internal Task PlayAsync(CancellationToken cancellationToken) => _device.Play(cancellationToken);

    internal Task PauseAsync(CancellationToken cancellationToken) => _device.Pause(cancellationToken);

    internal Task StopAsync(CancellationToken cancellationToken) => _device.Stop(cancellationToken);

    internal Task NextAsync(CancellationToken cancellationToken) => _device.Next(cancellationToken);

    internal Task PreviousAsync(CancellationToken cancellationToken) => _device.Previous(cancellationToken);

    internal Task TogglePlaybackAsync(CancellationToken cancellationToken) => _device.TogglePlayback(cancellationToken);

    internal Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        return AvTransport.Seek(new AVTransportService.SeekRequest { InstanceID = InstanceId, Unit = "REL_TIME", Target = SonosValues.FormatDuration(position) }, cancellationToken);
    }

    internal Task SetVolumeAsync(int volume, CancellationToken cancellationToken) =>
        RenderingControl.SetVolume(new RenderingControlService.SetVolumeRequest { InstanceID = InstanceId, Channel = MasterChannel, DesiredVolume = volume }, cancellationToken);

    internal Task ChangeVolumeAsync(int adjustment, CancellationToken cancellationToken) =>
        RenderingControl.SetRelativeVolume(new RenderingControlService.SetRelativeVolumeRequest { InstanceID = InstanceId, Channel = MasterChannel, Adjustment = adjustment }, cancellationToken);

    internal Task RampVolumeAsync(int volume, CancellationToken cancellationToken) =>
        RenderingControl.RampToVolume(new RenderingControlService.RampToVolumeRequest
        {
            InstanceID = InstanceId,
            Channel = MasterChannel,
            RampType = "SLEEP_TIMER_RAMP_TYPE",
            DesiredVolume = volume,
            ResetVolumeAfter = false,
            ProgramURI = string.Empty
        }, cancellationToken);

    internal Task SetMuteAsync(bool mute, CancellationToken cancellationToken) =>
        RenderingControl.SetMute(new RenderingControlService.SetMuteRequest { InstanceID = InstanceId, Channel = MasterChannel, DesiredMute = mute }, cancellationToken);

    internal Task SetTransportUriAsync(string uri, string metadata, CancellationToken cancellationToken) =>
        AvTransport.SetAVTransportURI(new AVTransportService.SetAVTransportURIRequest { InstanceID = InstanceId, CurrentURI = uri, CurrentURIMetaData = EscapeMetadataAmpersands(metadata) }, cancellationToken);

    internal async Task PlayFromQueueAsync(string uri, string metadata, CancellationToken cancellationToken)
    {
        await AvTransport.RemoveAllTracksFromQueue(cancellationToken);
        await AvTransport.AddURIToQueue(new AVTransportService.AddURIToQueueRequest
        {
            InstanceID = InstanceId,
            EnqueuedURI = uri,
            EnqueuedURIMetaData = EscapeMetadataAmpersands(metadata),
            DesiredFirstTrackNumberEnqueued = 0,
            EnqueueAsNext = false
        }, cancellationToken);
        await _device.SwitchToQueue(cancellationToken);
        await _device.Play(cancellationToken);
    }

    /// <summary>
    /// Queues an audio clip over the speaker's websocket. Each call uses a Sonos.Base device of its own: Sonos.Base
    /// keeps one websocket per device and cannot start it again once it failed or the speaker closed it, so a shared
    /// one would fail every later notification. The speaker's answer is not read, so a rejected clip is not reported.
    /// </summary>
    internal async Task PlayNotificationAsync(Uri soundUri, int volume, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NotificationTimeout);
        var device = new SonosBaseDevice(_deviceOptions);
        try
        {
            await device.QueueNotification(new NotificationOptions(soundUri, volume), timeout.Token);
        }
        finally
        {
            await CloseNotificationDeviceAsync(device);
        }
    }

    private async Task CloseNotificationDeviceAsync(SonosBaseDevice device)
    {
        // Closing gracefully lets the speaker take the clip before the connection goes; Sonos.Base waits for the
        // close without a token, so it gets a budget.
        var closing = device.DisposeAsync().AsTask();
        try
        {
            await closing.WaitAsync(NotificationCloseTimeout);
        }
        catch (Exception exception)
        {
            // Observed here or in the continuation below, whichever way the close ends.
            _ = closing.ContinueWith(static task => task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            _logger.LogDebug(exception, "Closing the Sonos notification websocket of {Uri} failed.", BaseUri);
        }
        finally
        {
            // Releases a socket that never opened or did not close in time; Sonos.Base's DisposeAsync skips both.
            device.Dispose();
        }
    }

    internal Task SwitchToTvAsync(CancellationToken cancellationToken) => _device.SwitchToSpdif(cancellationToken);

    internal Task SwitchToLineInAsync(CancellationToken cancellationToken) => _device.SwitchToLineIn(cancellationToken);

    internal Task SetPlayModeAsync(string playMode, CancellationToken cancellationToken) =>
        AvTransport.SetPlayMode(new AVTransportService.SetPlayModeRequest { InstanceID = InstanceId, NewPlayMode = playMode }, cancellationToken);

    /// <summary>
    /// Sets the sleep timer, at most <see cref="SonosValues.MaximumSleepTimer"/>; zero cancels it.
    /// </summary>
    internal Task SetSleepTimerAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, SonosValues.MaximumSleepTimer);
        return AvTransport.ConfigureSleepTimer(new AVTransportService.ConfigureSleepTimerRequest
        {
            InstanceID = InstanceId,
            NewSleepTimerDuration = duration == TimeSpan.Zero ? string.Empty : SonosValues.FormatDuration(duration)
        }, cancellationToken);
    }

    internal Task SetBassAsync(int bass, CancellationToken cancellationToken) =>
        RenderingControl.SetBass(new RenderingControlService.SetBassRequest { InstanceID = InstanceId, DesiredBass = bass }, cancellationToken);

    internal Task SetTrebleAsync(int treble, CancellationToken cancellationToken) =>
        RenderingControl.SetTreble(new RenderingControlService.SetTrebleRequest { InstanceID = InstanceId, DesiredTreble = treble }, cancellationToken);

    internal Task SetLoudnessAsync(bool loudness, CancellationToken cancellationToken) =>
        RenderingControl.SetLoudness(new RenderingControlService.SetLoudnessRequest { InstanceID = InstanceId, Channel = MasterChannel, DesiredLoudness = loudness }, cancellationToken);

    internal Task SetEqualizerAsync(string type, bool enabled, CancellationToken cancellationToken) =>
        RenderingControl.SetEQ(new RenderingControlService.SetEQRequest { InstanceID = InstanceId, EQType = type, DesiredValue = enabled ? 1 : 0 }, cancellationToken);

    internal Task JoinAsync(string coordinatorUuid, CancellationToken cancellationToken) =>
        SetTransportUriAsync($"x-rincon:{coordinatorUuid}", string.Empty, cancellationToken);

    internal Task LeaveGroupAsync(CancellationToken cancellationToken) =>
        AvTransport.BecomeCoordinatorOfStandaloneGroup(cancellationToken);

    // Both group volume commands scale the members from the last snapshot, so a fresh one keeps the current volume
    // ratio between them; without it, a member changed in the Sonos app since would jump back.

    internal async Task SetGroupVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        await GroupRenderingControl.SnapshotGroupVolume(cancellationToken);
        await GroupRenderingControl.SetGroupVolume(new GroupRenderingControlService.SetGroupVolumeRequest { InstanceID = InstanceId, DesiredVolume = volume }, cancellationToken);
    }

    internal async Task ChangeGroupVolumeAsync(int adjustment, CancellationToken cancellationToken)
    {
        await GroupRenderingControl.SnapshotGroupVolume(cancellationToken);
        await GroupRenderingControl.SetRelativeGroupVolume(new GroupRenderingControlService.SetRelativeGroupVolumeRequest { InstanceID = InstanceId, Adjustment = adjustment }, cancellationToken);
    }

    internal Task SetGroupMuteAsync(bool mute, CancellationToken cancellationToken) =>
        GroupRenderingControl.SetGroupMute(new GroupRenderingControlService.SetGroupMuteRequest { InstanceID = InstanceId, DesiredMute = mute }, cancellationToken);

    // Sonos.Base writes an argument containing '<' raw, escaping only quotes and angle brackets, so an ampersand
    // in DIDL metadata would reach the speaker unescaped and break its DIDL parse.
    private static string EscapeMetadataAmpersands(string metadata) =>
        metadata.Contains('<') ? metadata.Replace("&", "&amp;", StringComparison.Ordinal) : metadata;

    private async Task<bool?> GetEqualizerAsync(string type, string action, List<SonosReadFault> faults, CancellationToken cancellationToken)
    {
        var response = await ReadOrDefaultAsync(action,
            () => RenderingControl.GetEQ(new RenderingControlService.GetEQRequest { InstanceID = InstanceId, EQType = type }, cancellationToken), faults);
        return response is null ? null : SonosValues.IsLevelOn(response.CurrentValue);
    }

    public void Dispose() => _device.Dispose();
}
