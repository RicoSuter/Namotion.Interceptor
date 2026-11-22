using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Locks;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Hyundai;

/// <summary>
/// The doors, windows, lock and tires of a Bluelink vehicle. Doors and windows are named relative to the driver and assume a
/// left-hand drive vehicle: the left rear door and window are on the driver side. Tires keep the physical sides.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class BluelinkVehicleBody : IDoorLockState, ITitleProvider, IIconProvider
{
    internal BluelinkVehicleBody()
    {
        LockState = null;
        IsDriverFrontDoorOpen = null;
        IsPassengerFrontDoorOpen = null;
        IsDriverRearDoorOpen = null;
        IsPassengerRearDoorOpen = null;
        IsTrunkOpen = null;
        IsHoodOpen = null;
        IsDriverFrontWindowOpen = null;
        IsPassengerFrontWindowOpen = null;
        IsDriverRearWindowOpen = null;
        IsPassengerRearWindowOpen = null;
        FrontLeftTire = new BluelinkTire("Front left tire");
        FrontRightTire = new BluelinkTire("Front right tire");
        RearLeftTire = new BluelinkTire("Rear left tire");
        RearRightTire = new BluelinkTire("Rear right tire");
    }

    public string Title => "Body";

    public string IconName => "DirectionsCar";

    [State(IsDiscrete = true, Position = 1)] public partial DoorLockState? LockState { get; internal set; }
    [State(IsDiscrete = true, Position = 2)] public partial bool? IsDriverFrontDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 3)] public partial bool? IsPassengerFrontDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 4)] public partial bool? IsDriverRearDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 5)] public partial bool? IsPassengerRearDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 6)] public partial bool? IsTrunkOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 7)] public partial bool? IsHoodOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 8)] public partial bool? IsDriverFrontWindowOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 9)] public partial bool? IsPassengerFrontWindowOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 10)] public partial bool? IsDriverRearWindowOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 11)] public partial bool? IsPassengerRearWindowOpen { get; internal set; }

    /// <summary>Whether any door is open; null when a door is unknown and none is open.</summary>
    [Derived]
    [State(IsDiscrete = true, Position = 12)]
    public bool? IsAnyDoorOpen => IsAnyOpen([IsDriverFrontDoorOpen, IsPassengerFrontDoorOpen, IsDriverRearDoorOpen, IsPassengerRearDoorOpen]);

    /// <summary>Whether any window is open or vented; null when a window is unknown and none is open.</summary>
    [Derived]
    [State(IsDiscrete = true, Position = 13)]
    public bool? IsAnyWindowOpen => IsAnyOpen([IsDriverFrontWindowOpen, IsPassengerFrontWindowOpen, IsDriverRearWindowOpen, IsPassengerRearWindowOpen]);

    [State(Position = 14)] public partial BluelinkTire FrontLeftTire { get; internal set; }
    [State(Position = 15)] public partial BluelinkTire FrontRightTire { get; internal set; }
    [State(Position = 16)] public partial BluelinkTire RearLeftTire { get; internal set; }
    [State(Position = 17)] public partial BluelinkTire RearRightTire { get; internal set; }

    internal void Apply(BluelinkVehicleStatus status)
    {
        LockState = status.LockState;
        IsDriverFrontDoorOpen = status.IsDriverFrontDoorOpen;
        IsPassengerFrontDoorOpen = status.IsPassengerFrontDoorOpen;
        IsDriverRearDoorOpen = status.IsDriverRearDoorOpen;
        IsPassengerRearDoorOpen = status.IsPassengerRearDoorOpen;
        IsTrunkOpen = status.IsTrunkOpen;
        IsHoodOpen = status.IsHoodOpen;
        IsDriverFrontWindowOpen = status.IsDriverFrontWindowOpen;
        IsPassengerFrontWindowOpen = status.IsPassengerFrontWindowOpen;
        IsDriverRearWindowOpen = status.IsDriverRearWindowOpen;
        IsPassengerRearWindowOpen = status.IsPassengerRearWindowOpen;
        FrontLeftTire.Apply(status.FrontLeftTire);
        FrontRightTire.Apply(status.FrontRightTire);
        RearLeftTire.Apply(status.RearLeftTire);
        RearRightTire.Apply(status.RearRightTire);
    }

    private static bool? IsAnyOpen(ReadOnlySpan<bool?> states)
    {
        var isAnyUnknown = false;
        foreach (var state in states)
        {
            if (state == true)
            {
                return true;
            }

            isAnyUnknown |= state is null;
        }

        return isAnyUnknown ? null : false;
    }
}
