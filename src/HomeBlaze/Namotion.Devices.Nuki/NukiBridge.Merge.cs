using System.Globalization;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Model;

namespace Namotion.Devices.Nuki;

public partial class NukiBridge
{
    private void ApplyInfo(NukiInfoResponse info)
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
    /// Updates the existing devices and creates new ones. A collection is replaced only when its set of instances changed.
    /// </summary>
    internal void MergeDevices(IReadOnlyList<NukiDeviceResponse> devices)
    {
        var existingLocks = Locks;
        var existingOpeners = Openers;
        var locks = new Dictionary<string, NukiSmartLock>();
        var openers = new Dictionary<string, NukiOpener>();
        var isLockCreated = false;
        var isOpenerCreated = false;
        foreach (var device in devices)
        {
            var deviceType = (NukiDeviceType)device.DeviceType;
            if (deviceType is NukiDeviceType.SmartLock or NukiDeviceType.SmartDoor or NukiDeviceType.SmartLock3)
            {
                isLockCreated |= MergeLock(device, deviceType, existingLocks, locks);
            }
            else if (deviceType == NukiDeviceType.Opener)
            {
                isOpenerCreated |= MergeOpener(device, existingOpeners, openers);
            }
            else
            {
                _logger.LogDebug("Skipping Nuki device {NukiId} of unsupported type {DeviceType}.", device.NukiId, device.DeviceType);
            }
        }

        // A reused instance keeps its key, so the set of instances is unchanged when none was created or dropped.
        if (isLockCreated || locks.Count != existingLocks.Count)
        {
            Locks = locks;
        }

        if (isOpenerCreated || openers.Count != existingOpeners.Count)
        {
            Openers = openers;
        }
    }

    // Returns whether the lock was created.
    private bool MergeLock(
        NukiDeviceResponse device, NukiDeviceType deviceType, Dictionary<string, NukiSmartLock> existing, Dictionary<string, NukiSmartLock> locks)
    {
        var key = device.NukiId.ToString(CultureInfo.InvariantCulture);
        var isCreated = false;
        if (existing.GetValueOrDefault(key) is not { } smartLock || smartLock.DeviceType != deviceType)
        {
            smartLock = new NukiSmartLock(this, device.NukiId, deviceType);
            isCreated = true;
        }

        smartLock.Update(device);
        locks[key] = smartLock;
        return isCreated;
    }

    // Returns whether the Opener was created.
    private bool MergeOpener(NukiDeviceResponse device, Dictionary<string, NukiOpener> existing, Dictionary<string, NukiOpener> openers)
    {
        var key = device.NukiId.ToString(CultureInfo.InvariantCulture);
        var opener = existing.GetValueOrDefault(key);
        var isCreated = opener is null;
        opener ??= new NukiOpener(this, device.NukiId);
        opener.Update(device);
        openers[key] = opener;
        return isCreated;
    }
}
