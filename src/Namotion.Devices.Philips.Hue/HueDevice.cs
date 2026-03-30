using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using HueApi.Models;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Base device subject for Philips Hue devices.
/// Updated by the bridge via polling and event stream.
/// </summary>
[InterceptorSubject]
public partial class HueDevice :
    IConnectionState,
    IMonitoredService,
    IUnknownDevice,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    internal Device Device { get; private set; }

    internal ZigbeeConnectivity? ZigbeeConnectivity { get; private set; }

    public HueBridge Bridge { get; }

    public Guid ResourceId => Device.Id;

    [State]
    public partial DateTimeOffset? LastUpdated { get; set; }

    [Derived]
    public virtual string? Title => Device?.Metadata?.Name ?? "n/a";

    [Derived]
    public virtual string? IconName => "QuestionMark";

    [Derived]
    public virtual string? IconColor => IsConnected ? "Default" : "Error";

    [Derived]
    public bool IsConnected =>
        ZigbeeConnectivity == null ||
        ZigbeeConnectivity.Status == ConnectivityStatus.connected;

    [Derived]
    public ServiceStatus Status =>
        IsConnected ? ServiceStatus.Running : ServiceStatus.Error;

    [Derived]
    public string? StatusMessage =>
        IsConnected ? null : "Device disconnected";

    [Derived]
    [State]
    public bool? IsCertified => Device?.ProductData?.Certified;

    [Derived]
    [State]
    public string? ProductName => Device?.ProductData?.ProductName;

    [Derived]
    [State]
    public string? HardwarePlatformType => Device?.ProductData?.HardwarePlatformType;

    [Derived]
    [State]
    public string? SoftwareVersion => Device?.ProductData?.SoftwareVersion;

    [Derived]
    [State]
    public string? ManufacturerName => Device?.ProductData?.ManufacturerName;

    [Derived]
    [State]
    public string? ModelId => Device?.ProductData?.ModelId;

    public HueDevice(Device device, ZigbeeConnectivity? zigbeeConnectivity, HueBridge bridge)
    {
        Bridge = bridge;
        Device = device;
        ZigbeeConnectivity = zigbeeConnectivity;
        LastUpdated = DateTimeOffset.Now;
    }

    internal virtual HueDevice Update(Device device, ZigbeeConnectivity? zigbeeConnectivity)
    {
        Device = device;
        ZigbeeConnectivity = zigbeeConnectivity;
        LastUpdated = DateTimeOffset.Now;
        return this;
    }
}
