using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Model;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Nuki;

/// <summary>
/// A Nuki Smart Lock or Smart Door paired with a bridge.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class NukiSmartLock : NukiDevice, IDoorLock, IDoorLatchController, IDoorSensor, IBatteryState
{
    internal NukiSmartLock(NukiBridge bridge, long nukiId, NukiDeviceType deviceType)
        : base(bridge, nukiId, deviceType)
    {
        LockState = null;
        DoorState = null;
        BatteryLevel = null;
        IsBatteryCharging = null;
        IsKeypadBatteryCritical = null;
    }

    public partial DoorLockState? LockState { get; internal set; }

    public partial DoorState? DoorState { get; internal set; }

    [Category("Battery")]
    public partial decimal? BatteryLevel { get; internal set; }

    /// <summary>Whether the batteries of the lock are charging.</summary>
    [State(Position = 342)]
    [Category("Battery")]
    public partial bool? IsBatteryCharging { get; internal set; }

    /// <summary>Whether the batteries of the paired keypad are at a critical level.</summary>
    [State(Position = 343)]
    [Category("Battery")]
    public partial bool? IsKeypadBatteryCritical { get; internal set; }

    [Derived]
    public override string? IconName => LockState switch
    {
        DoorLockState.Locked => "Lock",
        DoorLockState.Jammed => "LockReset",
        _ => "LockOpen"
    };

    [Derived]
    public override string? IconColor => LockState switch
    {
        DoorLockState.Jammed => "Error",
        DoorLockState.Unlocked or DoorLockState.Unlatched => "Warning",
        _ => null
    };

    [Derived]
    [PropertyAttribute("Lock", KnownAttributes.IsEnabled)]
    public bool Lock_IsEnabled => IsConnected && LockState is DoorLockState.Unlocked or DoorLockState.Unlatched or DoorLockState.Jammed;

    [Derived]
    [PropertyAttribute("Unlock", KnownAttributes.IsEnabled)]
    public bool Unlock_IsEnabled => IsConnected && LockState is DoorLockState.Locked or DoorLockState.Jammed;

    [Derived]
    [PropertyAttribute("ToggleLock", KnownAttributes.IsEnabled)]
    public bool ToggleLock_IsEnabled => Unlatch_IsEnabled;

    [Derived]
    [PropertyAttribute("Unlatch", KnownAttributes.IsEnabled)]
    public bool Unlatch_IsEnabled => IsConnected && LockState is DoorLockState.Locked or DoorLockState.Unlocked;

    [Derived]
    [PropertyAttribute("LockNGo", KnownAttributes.IsEnabled)]
    public bool LockNGo_IsEnabled => IsConnected && LockState is DoorLockState.Locked;

    [Derived]
    [PropertyAttribute("LockNGoWithUnlatch", KnownAttributes.IsEnabled)]
    public bool LockNGoWithUnlatch_IsEnabled => LockNGo_IsEnabled;

    public Task LockAsync(CancellationToken cancellationToken) =>
        RunLockActionAsync(NukiLockAction.Lock, DoorLockState.Locking, "The lock could not lock the door.", cancellationToken);

    public Task UnlockAsync(CancellationToken cancellationToken) =>
        RunLockActionAsync(NukiLockAction.Unlock, DoorLockState.Unlocking, "The lock could not unlock the door.", cancellationToken);

    public Task UnlatchAsync(CancellationToken cancellationToken) =>
        RunLockActionAsync(NukiLockAction.Unlatch, DoorLockState.Unlatching, "The lock could not unlatch the door.", cancellationToken);

    [Operation(
        Title = "Lock 'n' Go",
        Description = "Unlocks the door and locks it again after a short time.",
        Icon = "Timer",
        RequiresConfirmation = true,
        Position = 10)]
    [Category("Lock")]
    public Task LockNGoAsync(CancellationToken cancellationToken) =>
        RunLockActionAsync(NukiLockAction.LockNGo, DoorLockState.Unlocking, "The lock could not run lock 'n' go.", cancellationToken);

    [Operation(
        Title = "Lock 'n' Go with unlatch",
        Description = "Unlocks the door, pulls the latch and locks the door again after a short time.",
        Icon = "Timer",
        RequiresConfirmation = true,
        Position = 11)]
    [Category("Lock")]
    public Task LockNGoWithUnlatchAsync(CancellationToken cancellationToken) =>
        RunLockActionAsync(NukiLockAction.LockNGoWithUnlatch, DoorLockState.Unlatching, "The lock could not run lock 'n' go with unlatch.", cancellationToken);

    private protected override void UpdateState(NukiLastKnownStateResponse? state)
    {
        LockState = NukiStateMapping.ToDoorLockState(state?.State);
        DoorState = NukiStateMapping.ToDoorState(state?.DoorSensorState);
        BatteryLevel = state?.BatteryChargeState is { } charge ? charge / 100m : null;
        IsBatteryCharging = state?.BatteryCharging;
        IsKeypadBatteryCritical = state?.KeypadBatteryCritical;
    }

    private Task RunLockActionAsync(NukiLockAction action, DoorLockState transition, string failureMessage, CancellationToken cancellationToken) =>
        RunActionAsync((int)action, () => LockState = transition, () => LockState = null, failureMessage, cancellationToken);
}
