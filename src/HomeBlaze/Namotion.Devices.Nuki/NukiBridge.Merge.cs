using System.Globalization;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Model;

namespace Namotion.Devices.Nuki;

public partial class NukiBridge
{
    internal void ApplyInfo(NukiInfoResponse info)
    {
        BridgeType = NukiStateMapping.ToBridgeType(info.BridgeType);
        HardwareId = info.Ids?.HardwareId;
        ServerId = info.Ids?.ServerId;
        SoftwareVersion = info.Versions?.FirmwareVersion ?? info.Versions?.AppVersion;
        WifiFirmwareVersion = info.Versions?.WifiFirmwareVersion;
        Uptime = info.Uptime is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        IsServerConnected = info.ServerConnected;
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
