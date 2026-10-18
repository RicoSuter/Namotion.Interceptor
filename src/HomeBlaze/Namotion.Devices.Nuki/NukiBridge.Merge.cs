using System.Globalization;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Model;

namespace Namotion.Devices.Nuki;

public partial class NukiBridge
{
    internal void ThrowIfNotConnected()
    {
        if (_client is null)
        {
            throw new InvalidOperationException("The bridge is not connected. " + (StatusMessage ?? string.Empty));
        }
    }

    /// <summary>
    /// Runs a lock action of a device. The transition is entered once the request gate is held, so that a poll that is
    /// still running cannot overwrite it with the state from before the action.
    /// </summary>
    internal async Task<NukiActionResponse> ExecuteActionAsync(
        NukiDevice device, int action, Action? enterTransition, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("The bridge is not connected.");
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            enterTransition?.Invoke();
            return await client.LockActionAsync(device.NukiId, (int)device.DeviceType, action, cancellationToken);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Reads the device list and updates the devices.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("The bridge is not connected.");
        await RefreshListAsync(client, cancellationToken);
        LastUpdated = DateTimeOffset.UtcNow;
    }

    private async Task RefreshListAsync(NukiBridgeClient client, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            MergeDevices(await client.GetListAsync(cancellationToken));
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task RefreshInfoAsync(NukiBridgeClient client, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            ApplyInfo(await client.GetInfoAsync(cancellationToken));
        }
        finally
        {
            _requestGate.Release();
        }
    }

    internal void ApplyInfo(NukiInfoResponse info)
    {
        BridgeType = ToBridgeType(info.BridgeType);
        HardwareId = info.Ids?.HardwareId;
        ServerId = info.Ids?.ServerId;
        ApplyVersions(info.Versions);
        Uptime = info.Uptime is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        IsServerConnected = info.ServerConnected;
    }

    private static NukiBridgeType? ToBridgeType(int bridgeType) => bridgeType switch
    {
        1 => NukiBridgeType.Hardware,
        2 => NukiBridgeType.Software,
        _ => null
    };

    private void ApplyVersions(NukiVersionsResponse? versions)
    {
        SoftwareVersion = versions?.FirmwareVersion ?? versions?.AppVersion;
        WifiFirmwareVersion = versions?.WifiFirmwareVersion;
    }

    /// <summary>
    /// Updates the existing devices and creates new ones. A collection is replaced only when its set of devices changed.
    /// </summary>
    internal void MergeDevices(IReadOnlyList<NukiDeviceResponse> devices)
    {
        var existingLocks = Locks;
        var existingOpeners = Openers;
        var locks = new Dictionary<string, NukiSmartLock>();
        var openers = new Dictionary<string, NukiOpener>();

        foreach (var device in devices)
        {
            MergeDevice(device, existingLocks, locks, existingOpeners, openers);
        }

        if (!HaveSameKeys(existingLocks, locks))
        {
            Locks = locks;
        }

        if (!HaveSameKeys(existingOpeners, openers))
        {
            Openers = openers;
        }
    }

    private void MergeDevice(
        NukiDeviceResponse device,
        Dictionary<string, NukiSmartLock> existingLocks,
        Dictionary<string, NukiSmartLock> locks,
        Dictionary<string, NukiOpener> existingOpeners,
        Dictionary<string, NukiOpener> openers)
    {
        var deviceType = (NukiDeviceType)device.DeviceType;
        var key = device.NukiId.ToString(CultureInfo.InvariantCulture);
        if (IsLockDeviceType(deviceType))
        {
            var smartLock = existingLocks.GetValueOrDefault(key) ?? new NukiSmartLock(this, device.NukiId, deviceType);
            smartLock.Update(device);
            locks[key] = smartLock;
        }
        else if (deviceType == NukiDeviceType.Opener)
        {
            var opener = existingOpeners.GetValueOrDefault(key) ?? new NukiOpener(this, device.NukiId);
            opener.Update(device);
            openers[key] = opener;
        }
        else
        {
            Logger.LogDebug("Skipping Nuki device {NukiId} of unsupported type {DeviceType}.", device.NukiId, device.DeviceType);
        }
    }

    private static bool IsLockDeviceType(NukiDeviceType deviceType) =>
        deviceType is NukiDeviceType.SmartLock or NukiDeviceType.SmartDoor or NukiDeviceType.SmartLock3;

    private static bool HaveSameKeys<T>(Dictionary<string, T> existing, Dictionary<string, T> updated)
    {
        if (existing.Count != updated.Count)
        {
            return false;
        }

        foreach (var key in existing.Keys)
        {
            if (!updated.ContainsKey(key))
            {
                return false;
            }
        }

        return true;
    }
}
