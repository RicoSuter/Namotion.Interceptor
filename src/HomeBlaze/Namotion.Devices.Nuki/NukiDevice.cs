using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Networking;
using Namotion.Devices.Nuki.Model;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Nuki;

/// <summary>
/// A device paired with a Nuki bridge. Updated by the bridge from its device list.
/// </summary>
[InterceptorSubject]
public abstract partial class NukiDevice :
    IConnectionState,
    IMonitoredService,
    ISoftwareState,
    ILastUpdatedProvider,
    ITitleProvider,
    IIconProvider
{
    private protected NukiDevice(NukiBridge bridge, long nukiId, NukiDeviceType deviceType)
    {
        Bridge = bridge;
        NukiId = nukiId;
        DeviceType = deviceType;

        Name = null;
        HasLastKnownState = false;
        SoftwareVersion = null;
        IsBatteryCritical = null;
        LastUpdated = null;
    }

    // A plain property, not an intercepted one: an intercepted reference would attach the bridge below its own device.
    internal NukiBridge Bridge { get; }

    /// <summary>The ID of the device at the bridge.</summary>
    [State(Position = 1)]
    [Category("Device")]
    public long NukiId { get; }

    /// <summary>The type of the device.</summary>
    [State(Position = 2)]
    [Category("Device")]
    public NukiDeviceType DeviceType { get; }

    internal partial string? Name { get; set; }

    internal partial bool HasLastKnownState { get; set; }

    public partial string? SoftwareVersion { get; internal set; }

    /// <summary>Whether the batteries of the device are at a critical level.</summary>
    [State(Position = 341)]
    [Category("Battery")]
    public partial bool? IsBatteryCritical { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    public string? Title => Name ?? "Nuki " + NukiId;

    [Derived]
    public bool IsConnected => Bridge.IsConnected && HasLastKnownState;

    [Derived]
    public ServiceStatus Status => IsConnected ? ServiceStatus.Running : ServiceStatus.Error;

    [Derived]
    public string? StatusMessage => (IsConnected, Bridge.IsConnected) switch
    {
        (true, _) => null,
        (false, true) => "The bridge reports no state for the device.",
        (false, false) => "The bridge is not connected."
    };

    [Derived]
    public virtual string? IconName => "Lock";

    [Derived]
    public virtual string? IconColor => null;

    internal void Update(NukiDeviceResponse device)
    {
        Name = device.Name;
        SoftwareVersion = device.FirmwareVersion;

        var state = device.LastKnownState;
        HasLastKnownState = state is not null;
        IsBatteryCritical = state?.BatteryCritical;
        LastUpdated = state?.Timestamp;
        UpdateState(state);
    }

    private protected abstract void UpdateState(NukiLastKnownStateResponse? state);

    /// <summary>
    /// Runs a lock action through <see cref="NukiBridge.ExecuteActionAsync"/> and throws an
    /// <see cref="InvalidOperationException"/> with <paramref name="failureMessage"/> when the device reports a failure.
    /// </summary>
    private protected async Task RunActionAsync(
        int action,
        Action? enterTransition,
        Action resetState,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var result = await Bridge.ExecuteActionAsync(this, action, enterTransition, resetState, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }
}
