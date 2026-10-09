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

    private readonly HttpClient _httpClient;
    private readonly SonosBaseDevice _device;

    internal SonosConnection(Uri baseUri, string? uuid, HttpClient httpClient, ISonosServiceProvider provider)
    {
        BaseUri = baseUri;
        _httpClient = httpClient;
        _device = new SonosBaseDevice(new SonosDeviceOptions(baseUri, provider, uuid));
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

    internal async Task<SonosZoneInfo> ReadZoneInfoAsync(CancellationToken cancellationToken)
    {
        var zoneInfo = await _device.DevicePropertiesService.GetZoneInfo(cancellationToken);
        return new SonosZoneInfo(zoneInfo.SerialNumber, zoneInfo.MACAddress, zoneInfo.HardwareVersion, zoneInfo.DisplaySoftwareVersion);
    }

    internal async Task<SonosTopology> ReadTopologyAsync(CancellationToken cancellationToken)
    {
        var response = await _device.ZoneGroupTopologyService.GetZoneGroupState(cancellationToken);
        return ZoneGroupStateParser.Parse(response.ZoneGroupState);
    }

    internal async Task<IReadOnlyList<SonosFavorite>> ReadFavoritesAsync(CancellationToken cancellationToken)
    {
        var response = await _device.ContentDirectoryService.Browse("FV:2", Count: 100, cancellationToken: cancellationToken);
        return FavoritesParser.Parse(response.Result, BaseUri);
    }

    /// <summary>
    /// Reads one player. Requests go one after another: a poll is a dozen small calls, and players are polled in
    /// parallel already.
    /// </summary>
    internal async Task<SonosPlayerReading> ReadPlayerAsync(bool isHomeTheater, CancellationToken cancellationToken)
    {
        var transport = await AvTransport.GetTransportInfo(cancellationToken);
        var settings = await AvTransport.GetTransportSettings(cancellationToken);
        var media = await AvTransport.GetMediaInfo(cancellationToken);
        var position = await AvTransport.GetPositionInfo(cancellationToken);
        var sleepTimer = await AvTransport.GetRemainingSleepTimerDuration(cancellationToken);

        var volume = await RenderingControl.GetVolume(new RenderingControlService.GetVolumeRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken);
        var mute = await RenderingControl.GetMute(new RenderingControlService.GetMuteRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken);
        var bass = await RenderingControl.GetBass(cancellationToken);
        var treble = await RenderingControl.GetTreble(cancellationToken);
        var loudness = await RenderingControl.GetLoudness(new RenderingControlService.GetLoudnessRequest { InstanceID = InstanceId, Channel = MasterChannel }, cancellationToken);

        bool? nightMode = null;
        bool? speechEnhancement = null;
        if (isHomeTheater)
        {
            nightMode = await GetEqualizerAsync("NightMode", cancellationToken);
            speechEnhancement = await GetEqualizerAsync("DialogLevel", cancellationToken);
        }

        return new SonosPlayerReading(
            new AvTransportChange(
                transport.CurrentTransportState,
                settings.PlayMode,
                media.CurrentURI,
                position.TrackURI,
                position.TrackDuration,
                position.TrackMetaData),
            SonosValues.ParseDuration(position.RelTime),
            SonosValues.ParseDuration(sleepTimer.RemainingSleepTimerDuration),
            new RenderingControlChange(
                volume.CurrentVolume,
                mute.CurrentMute,
                bass.CurrentBass,
                treble.CurrentTreble,
                loudness.CurrentLoudness,
                nightMode,
                speechEnhancement));
    }

    internal async Task<GroupRenderingControlChange> ReadGroupAsync(CancellationToken cancellationToken)
    {
        var volume = await GroupRenderingControl.GetGroupVolume(cancellationToken);
        var mute = await GroupRenderingControl.GetGroupMute(cancellationToken);
        return new GroupRenderingControlChange(volume.CurrentVolume, mute.CurrentMute);
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

    internal Task<bool> PlayNotificationAsync(Uri soundUri, int volume, CancellationToken cancellationToken) =>
        _device.QueueNotification(new NotificationOptions(soundUri, volume), cancellationToken);

    internal Task SwitchToTvAsync(CancellationToken cancellationToken) => _device.SwitchToSpdif(cancellationToken);

    internal Task SwitchToLineInAsync(CancellationToken cancellationToken) => _device.SwitchToLineIn(cancellationToken);

    internal Task SetPlayModeAsync(string playMode, CancellationToken cancellationToken) =>
        AvTransport.SetPlayMode(new AVTransportService.SetPlayModeRequest { InstanceID = InstanceId, NewPlayMode = playMode }, cancellationToken);

    /// <summary>
    /// Sets the sleep timer; zero cancels it.
    /// </summary>
    internal Task SetSleepTimerAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
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

    internal async Task SetGroupVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        // The snapshot is what makes Sonos keep the volume ratio between the members.
        await GroupRenderingControl.SnapshotGroupVolume(cancellationToken);
        await GroupRenderingControl.SetGroupVolume(new GroupRenderingControlService.SetGroupVolumeRequest { InstanceID = InstanceId, DesiredVolume = volume }, cancellationToken);
    }

    internal Task ChangeGroupVolumeAsync(int adjustment, CancellationToken cancellationToken) =>
        GroupRenderingControl.SetRelativeGroupVolume(new GroupRenderingControlService.SetRelativeGroupVolumeRequest { InstanceID = InstanceId, Adjustment = adjustment }, cancellationToken);

    internal Task SetGroupMuteAsync(bool mute, CancellationToken cancellationToken) =>
        GroupRenderingControl.SetGroupMute(new GroupRenderingControlService.SetGroupMuteRequest { InstanceID = InstanceId, DesiredMute = mute }, cancellationToken);

    // Sonos.Base writes an argument containing '<' raw, escaping only quotes and angle brackets, so an ampersand
    // in DIDL metadata would reach the speaker unescaped and break its DIDL parse.
    private static string EscapeMetadataAmpersands(string metadata) =>
        metadata.Contains('<') ? metadata.Replace("&", "&amp;", StringComparison.Ordinal) : metadata;

    private async Task<bool> GetEqualizerAsync(string type, CancellationToken cancellationToken)
    {
        var response = await RenderingControl.GetEQ(new RenderingControlService.GetEQRequest { InstanceID = InstanceId, EQType = type }, cancellationToken);
        return response.CurrentValue == 1;
    }

    public void Dispose() => _device.Dispose();
}
