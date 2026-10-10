using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A physical Sonos unit: a room player or a satellite bonded to one.
/// </summary>
[InterceptorSubject]
public abstract partial class SonosDevice :
    IDeviceInfo,
    INetworkAdapter,
    ISoftwareState,
    IConnectionState,
    ITitleProvider,
    IIconProvider
{
    private const int PollFailuresUntilUnreachable = 2;

    private bool _hasStaticData;
    private string? _staticDataFirmwareBuild;

    // The failed polls since the last successful one. Polls report under the system's connection lock, but a
    // released connection does not, so it is only read and written atomically.
    private int _consecutivePollFailures;

    private protected SonosDevice(string uuid)
    {
        Uuid = uuid;
        RoomName = string.Empty;
        ServiceIds = new HashSet<string>(StringComparer.Ordinal);

        // Until its first successful poll, which also finds out whether there is a connection to command it through.
        IsReachable = false;
    }

    /// <summary>
    /// The RINCON identifier of the unit, stable across restarts and IP changes.
    /// </summary>
    [State(Position = 1)]
    public partial string Uuid { get; internal set; }

    [State(Position = 2)]
    public partial string RoomName { get; internal set; }

    internal partial Uri? BaseUri { get; set; }

    /// <summary>
    /// The firmware build from the topology; a change means the zone info has to be read again.
    /// </summary>
    internal partial string? FirmwareBuild { get; set; }

    internal partial IReadOnlySet<string> ServiceIds { get; set; }

    internal partial bool IsInTopology { get; set; }

    internal partial bool IsReachable { get; set; }

    [Derived]
    public string? Manufacturer => "Sonos";

    public partial string? Model { get; internal set; }

    public partial string? ProductCode { get; internal set; }

    public partial string? SerialNumber { get; internal set; }

    public partial string? HardwareRevision { get; internal set; }

    [Derived]
    public string? IpAddress => BaseUri?.Host;

    public partial string? MacAddress { get; internal set; }

    [Derived]
    public string? SubnetMask => null;

    [Derived]
    public string? Gateway => null;

    public partial bool? IsWireless { get; internal set; }

    [Derived]
    public int? SignalStrength => null;

    public partial string? SoftwareVersion { get; internal set; }

    [Derived]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    public bool IsConnected => IsInTopology && IsReachable;

    [State(Position = 901)]
    public partial string? StatusMessage { get; internal set; }

    [Derived]
    public virtual string? Title => $"{Model ?? "Sonos"} ({RoomName})";

    [Derived]
    public virtual string? IconName => "Speaker";

    [Derived]
    public string? IconColor => IsConnected ? null : "Error";

    internal bool NeedsStaticData => !_hasStaticData || _staticDataFirmwareBuild != FirmwareBuild;

    internal void ApplyTopology(string roomName, Uri baseUri, string? firmwareBuild, bool? isWireless)
    {
        RoomName = roomName;
        BaseUri = baseUri;
        FirmwareBuild = firmwareBuild;
        IsWireless = isWireless;
        IsInTopology = true;
    }

    /// <summary>
    /// Applies the device description alone, when the zone info could not be read; it is read again next time.
    /// </summary>
    internal virtual void ApplyDescription(SonosDeviceDescription description)
    {
        Model = description.ModelName;
        ProductCode = description.ModelNumber;
        ServiceIds = description.ServiceIds;
    }

    internal void ApplyStaticData(
        SonosDeviceDescription description,
        string? serialNumber,
        string? macAddress,
        string? hardwareRevision,
        string? softwareVersion)
    {
        ApplyDescription(description);
        SerialNumber = SonosValues.NullIfEmpty(serialNumber);
        MacAddress = SonosValues.NullIfEmpty(macAddress);
        HardwareRevision = SonosValues.NullIfEmpty(hardwareRevision);
        SoftwareVersion = SonosValues.NullIfEmpty(softwareVersion);
        _hasStaticData = true;
        _staticDataFirmwareBuild = FirmwareBuild;
    }

    internal void InvalidateStaticData() => _hasStaticData = false;

    internal void MarkMissing() => IsInTopology = false;

    internal void ReportPollSucceeded()
    {
        Volatile.Write(ref _consecutivePollFailures, 0);
        IsReachable = true;
        StatusMessage = null;
    }

    /// <summary>
    /// Records a failed poll. A reachable device stays reachable after one failed poll and becomes unreachable with
    /// the second in a row, since a single lost request says little about the speaker.
    /// </summary>
    /// <returns>Whether the device became unreachable or its message changed, so the caller logs it at Warning once.</returns>
    internal bool ReportPollFailed(string message)
    {
        var failures = Interlocked.Increment(ref _consecutivePollFailures);
        return (!IsReachable || failures >= PollFailuresUntilUnreachable) && MarkUnreachable(message);
    }

    /// <summary>
    /// Drops the state an outage invalidates once the device is unreachable. The caller holds no lock of the system.
    /// </summary>
    internal virtual void ForgetStateOfOutage()
    {
        // Identity and settings stay valid across an outage.
    }

    /// <summary>
    /// Makes the device unreachable at once, for a released connection rather than a failed poll.
    /// </summary>
    /// <returns>Whether the device became unreachable or its message changed.</returns>
    internal bool MarkUnreachable(string message)
    {
        var isNew = IsReachable || StatusMessage != message;
        IsReachable = false;
        StatusMessage = message;
        return isNew;
    }
}
