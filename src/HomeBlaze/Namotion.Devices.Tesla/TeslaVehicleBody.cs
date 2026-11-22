using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Locks;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The doors, windows, trunks, lock, modes and tires of a Tesla vehicle. Doors and windows are named like Tesla names them, relative to the driver.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaVehicleBody : IDoorLockState, ITitleProvider, IIconProvider
{
    // The cached snapshot has separate timestamps for the closures and the tires, the JSON has one for both.
    private readonly TeslaSectionClock _closuresClock = new();
    private readonly TeslaSectionClock _tireClock = new();

    public string Title => "Body";

    public string IconName => "DirectionsCar";

    [State(IsDiscrete = true, Position = 1)] public partial DoorLockState? LockState { get; internal set; }

    [State(IsDiscrete = true, Position = 2)] public partial bool? IsDriverFrontDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 3)] public partial bool? IsDriverRearDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 4)] public partial bool? IsPassengerFrontDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 5)] public partial bool? IsPassengerRearDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 6)] public partial bool? IsFrunkOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 7)] public partial bool? IsTrunkOpen { get; internal set; }

    [State(IsDiscrete = true, Position = 8)] public partial bool? IsDriverFrontWindowOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 9)] public partial bool? IsPassengerFrontWindowOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 10)] public partial bool? IsDriverRearWindowOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 11)] public partial bool? IsPassengerRearWindowOpen { get; internal set; }

    /// <summary>Whether any of the four doors is open. The frunk and the trunk are not included.</summary>
    [Derived]
    [State(IsDiscrete = true, Position = 12)]
    public bool? IsAnyDoorOpen => Any(IsDriverFrontDoorOpen, IsDriverRearDoorOpen, IsPassengerFrontDoorOpen, IsPassengerRearDoorOpen);

    [Derived]
    [State(IsDiscrete = true, Position = 13)]
    public bool? IsAnyWindowOpen => Any(IsDriverFrontWindowOpen, IsPassengerFrontWindowOpen, IsDriverRearWindowOpen, IsPassengerRearWindowOpen);

    [State(IsDiscrete = true, Position = 14)] public partial bool? IsUserPresent { get; internal set; }

    [State(IsDiscrete = true, Position = 15)] public partial bool? IsSentryModeOn { get; internal set; }
    [State(IsDiscrete = true, Position = 16)] public partial bool? IsSentryModeAvailable { get; internal set; }
    [State(IsDiscrete = true, Position = 17)] public partial bool? IsValetModeOn { get; internal set; }
    [State(IsDiscrete = true, Position = 18)] public partial bool? IsValetPinNeeded { get; internal set; }
    [State(IsDiscrete = true, Position = 19)] public partial bool? IsRemoteStartActive { get; internal set; }
    [State(IsDiscrete = true, Position = 20)] public partial bool? IsRemoteStartEnabled { get; internal set; }
    [State(IsDiscrete = true, Position = 21)] public partial bool? IsRemoteStartSupported { get; internal set; }
    [State(IsDiscrete = true, Position = 22)] public partial bool? IsInServiceMode { get; internal set; }
    [State(IsDiscrete = true, Position = 23)] public partial bool? IsInServiceModePlus { get; internal set; }

    /// <summary>The state of the dashcam as Tesla reports it, such as "Recording".</summary>
    [State(Position = 24)] public partial string? DashcamState { get; internal set; }

    [State(IsDiscrete = true, Position = 25)] public partial bool? IsDashcamClipSaveAvailable { get; internal set; }

    /// <summary>Tesla's number for the state of the center display: 0 is off.</summary>
    [State(Position = 26)] public partial int? CenterDisplayState { get; internal set; }

    [State(IsDiscrete = true, Position = 27)] public partial bool? IsWebcamAvailable { get; internal set; }

    /// <summary>Tesla's state of the automatic parking, such as "unavailable".</summary>
    [State(Position = 28)] public partial string? AutoparkState { get; internal set; }

    /// <summary>Tesla's number for the active Santa mode: 0 is off.</summary>
    [State(Position = 29)] public partial int? SantaMode { get; internal set; }

    [State(Position = 30)] public partial int? SelfTestProgress { get; internal set; }
    [State(IsDiscrete = true, Position = 31)] public partial bool? IsSelfTestRequested { get; internal set; }

    [State(Position = 32)] public partial TeslaTire FrontLeftTire { get; internal set; }
    [State(Position = 33)] public partial TeslaTire FrontRightTire { get; internal set; }
    [State(Position = 34)] public partial TeslaTire RearLeftTire { get; internal set; }
    [State(Position = 35)] public partial TeslaTire RearRightTire { get; internal set; }

    internal TeslaVehicleBody()
    {
        LockState = null;
        IsDriverFrontDoorOpen = null;
        IsDriverRearDoorOpen = null;
        IsPassengerFrontDoorOpen = null;
        IsPassengerRearDoorOpen = null;
        IsFrunkOpen = null;
        IsTrunkOpen = null;
        IsDriverFrontWindowOpen = null;
        IsPassengerFrontWindowOpen = null;
        IsDriverRearWindowOpen = null;
        IsPassengerRearWindowOpen = null;
        IsUserPresent = null;
        IsSentryModeOn = null;
        IsSentryModeAvailable = null;
        IsValetModeOn = null;
        IsValetPinNeeded = null;
        IsRemoteStartActive = null;
        IsRemoteStartEnabled = null;
        IsRemoteStartSupported = null;
        IsInServiceMode = null;
        IsInServiceModePlus = null;
        DashcamState = null;
        IsDashcamClipSaveAvailable = null;
        CenterDisplayState = null;
        IsWebcamAvailable = null;
        AutoparkState = null;
        SantaMode = null;
        SelfTestProgress = null;
        IsSelfTestRequested = null;
        FrontLeftTire = new TeslaTire("Front Left Tire");
        FrontRightTire = new TeslaTire("Front Right Tire");
        RearLeftTire = new TeslaTire("Rear Left Tire");
        RearRightTire = new TeslaTire("Rear Right Tire");
    }

    internal void Update(TeslaVehicleStateDto dto)
    {
        // Both clocks advance, so no check may short-circuit the other.
        var timestamp = TeslaValues.FromUnixMilliseconds(dto.Timestamp);
        var isClosuresNewer = _closuresClock.TryAdvance(timestamp);
        var isTireNewer = _tireClock.TryAdvance(timestamp);

        if (isClosuresNewer)
        {
            UpdateClosuresFromJson(dto);
            UpdateModesFromJson(dto);
            UpdateStatusFromJson(dto);
        }

        if (isTireNewer)
        {
            UpdateTiresFromJson(dto);
        }
    }

    internal void Update(ClosuresState proto)
    {
        if (!_closuresClock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateDoorsFromProtobuf(proto);
        UpdateWindowsFromProtobuf(proto);
        UpdateModesFromProtobuf(proto);
    }

    internal void Update(TirePressureState proto)
    {
        if (!_tireClock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdatePressuresFromProtobuf(proto);
        UpdateWarningsFromProtobuf(proto);
        UpdateLastSeenFromProtobuf(proto);
    }

    private static bool? Any(bool? first, bool? second, bool? third, bool? fourth)
    {
        if (first is null && second is null && third is null && fourth is null)
        {
            return null;
        }

        return first == true || second == true || third == true || fourth == true;
    }

    // Tesla reports an open closure as a number other than 0.
    private static bool? IsOpen(int? value) => value is { } number ? number != 0 : null;

    private static DoorLockState? Lock(bool? locked)
    {
        if (locked is null)
        {
            return null;
        }

        return locked.Value ? DoorLockState.Locked : DoorLockState.Unlocked;
    }

    private void UpdateClosuresFromJson(TeslaVehicleStateDto dto)
    {
        LockState = Lock(dto.Locked);
        IsDriverFrontDoorOpen = IsOpen(dto.Df);
        IsDriverRearDoorOpen = IsOpen(dto.Dr);
        IsPassengerFrontDoorOpen = IsOpen(dto.Pf);
        IsPassengerRearDoorOpen = IsOpen(dto.Pr);
        IsFrunkOpen = IsOpen(dto.Ft);
        IsTrunkOpen = IsOpen(dto.Rt);
        IsDriverFrontWindowOpen = IsOpen(dto.FdWindow);
        IsPassengerFrontWindowOpen = IsOpen(dto.FpWindow);
        IsDriverRearWindowOpen = IsOpen(dto.RdWindow);
        IsPassengerRearWindowOpen = IsOpen(dto.RpWindow);
    }

    private void UpdateModesFromJson(TeslaVehicleStateDto dto)
    {
        IsUserPresent = dto.IsUserPresent;
        IsSentryModeOn = dto.SentryMode;
        IsSentryModeAvailable = dto.SentryModeAvailable;
        IsValetModeOn = dto.ValetMode;
        IsValetPinNeeded = dto.ValetPinNeeded;
        IsRemoteStartActive = dto.RemoteStart;
        IsRemoteStartEnabled = dto.RemoteStartEnabled;
        IsRemoteStartSupported = dto.RemoteStartSupported;
    }

    // These fields have no counterpart in the cached snapshot.
    private void UpdateStatusFromJson(TeslaVehicleStateDto dto)
    {
        IsInServiceMode = dto.ServiceMode;
        IsInServiceModePlus = dto.ServiceModePlus;
        DashcamState = TeslaValues.Text(dto.DashcamState);
        IsDashcamClipSaveAvailable = dto.DashcamClipSaveAvailable;
        CenterDisplayState = dto.CenterDisplayState;
        IsWebcamAvailable = dto.WebcamAvailable;
        AutoparkState = TeslaValues.Text(dto.AutoparkStateV2);
        SantaMode = dto.SantaMode;
        SelfTestProgress = dto.VehicleSelfTestProgress;
        IsSelfTestRequested = dto.VehicleSelfTestRequested;
    }

    private void UpdateTiresFromJson(TeslaVehicleStateDto dto)
    {
        // The JSON has up to 3 decimals and the protobuf a float, so the JSON is rounded to float precision to give the same value.
        FrontLeftTire.Pressure = TeslaValues.ToFloatPrecision(dto.TpmsPressureFl);
        FrontRightTire.Pressure = TeslaValues.ToFloatPrecision(dto.TpmsPressureFr);
        RearLeftTire.Pressure = TeslaValues.ToFloatPrecision(dto.TpmsPressureRl);
        RearRightTire.Pressure = TeslaValues.ToFloatPrecision(dto.TpmsPressureRr);

        var frontRecommended = TeslaValues.ToFloatPrecision(dto.TpmsRcpFrontValue);
        var rearRecommended = TeslaValues.ToFloatPrecision(dto.TpmsRcpRearValue);
        FrontLeftTire.RecommendedPressure = frontRecommended;
        FrontRightTire.RecommendedPressure = frontRecommended;
        RearLeftTire.RecommendedPressure = rearRecommended;
        RearRightTire.RecommendedPressure = rearRecommended;

        FrontLeftTire.HasSoftWarning = dto.TpmsSoftWarningFl;
        FrontRightTire.HasSoftWarning = dto.TpmsSoftWarningFr;
        RearLeftTire.HasSoftWarning = dto.TpmsSoftWarningRl;
        RearRightTire.HasSoftWarning = dto.TpmsSoftWarningRr;
        FrontLeftTire.HasHardWarning = dto.TpmsHardWarningFl;
        FrontRightTire.HasHardWarning = dto.TpmsHardWarningFr;
        RearLeftTire.HasHardWarning = dto.TpmsHardWarningRl;
        RearRightTire.HasHardWarning = dto.TpmsHardWarningRr;

        FrontLeftTire.LastSeen = TeslaValues.FromUnixSeconds(dto.TpmsLastSeenPressureTimeFl);
        FrontRightTire.LastSeen = TeslaValues.FromUnixSeconds(dto.TpmsLastSeenPressureTimeFr);
        RearLeftTire.LastSeen = TeslaValues.FromUnixSeconds(dto.TpmsLastSeenPressureTimeRl);
        RearRightTire.LastSeen = TeslaValues.FromUnixSeconds(dto.TpmsLastSeenPressureTimeRr);
    }

    private void UpdateDoorsFromProtobuf(ClosuresState proto)
    {
        if (proto.OptionalLockedCase != ClosuresState.OptionalLockedOneofCase.None) LockState = Lock(proto.Locked);
        if (proto.OptionalDoorOpenDriverFrontCase != ClosuresState.OptionalDoorOpenDriverFrontOneofCase.None) IsDriverFrontDoorOpen = proto.DoorOpenDriverFront;
        if (proto.OptionalDoorOpenDriverRearCase != ClosuresState.OptionalDoorOpenDriverRearOneofCase.None) IsDriverRearDoorOpen = proto.DoorOpenDriverRear;
        if (proto.OptionalDoorOpenPassengerFrontCase != ClosuresState.OptionalDoorOpenPassengerFrontOneofCase.None) IsPassengerFrontDoorOpen = proto.DoorOpenPassengerFront;
        if (proto.OptionalDoorOpenPassengerRearCase != ClosuresState.OptionalDoorOpenPassengerRearOneofCase.None) IsPassengerRearDoorOpen = proto.DoorOpenPassengerRear;
        if (proto.OptionalDoorOpenTrunkFrontCase != ClosuresState.OptionalDoorOpenTrunkFrontOneofCase.None) IsFrunkOpen = proto.DoorOpenTrunkFront;
        if (proto.OptionalDoorOpenTrunkRearCase != ClosuresState.OptionalDoorOpenTrunkRearOneofCase.None) IsTrunkOpen = proto.DoorOpenTrunkRear;
    }

    private void UpdateWindowsFromProtobuf(ClosuresState proto)
    {
        if (proto.OptionalWindowOpenDriverFrontCase != ClosuresState.OptionalWindowOpenDriverFrontOneofCase.None) IsDriverFrontWindowOpen = proto.WindowOpenDriverFront;
        if (proto.OptionalWindowOpenPassengerFrontCase != ClosuresState.OptionalWindowOpenPassengerFrontOneofCase.None) IsPassengerFrontWindowOpen = proto.WindowOpenPassengerFront;
        if (proto.OptionalWindowOpenDriverRearCase != ClosuresState.OptionalWindowOpenDriverRearOneofCase.None) IsDriverRearWindowOpen = proto.WindowOpenDriverRear;
        if (proto.OptionalWindowOpenPassengerRearCase != ClosuresState.OptionalWindowOpenPassengerRearOneofCase.None) IsPassengerRearWindowOpen = proto.WindowOpenPassengerRear;
    }

    // The sentry mode and the center display state of the snapshot are enums with their own states, not the on/off and the number of the JSON, so only the JSON sets them.
    private void UpdateModesFromProtobuf(ClosuresState proto)
    {
        if (proto.OptionalIsUserPresentCase != ClosuresState.OptionalIsUserPresentOneofCase.None) IsUserPresent = proto.IsUserPresent;
        if (proto.OptionalSentryModeAvailableCase != ClosuresState.OptionalSentryModeAvailableOneofCase.None) IsSentryModeAvailable = proto.SentryModeAvailable;
        if (proto.OptionalValetModeCase != ClosuresState.OptionalValetModeOneofCase.None) IsValetModeOn = proto.ValetMode;
        if (proto.OptionalValetPinNeededCase != ClosuresState.OptionalValetPinNeededOneofCase.None) IsValetPinNeeded = proto.ValetPinNeeded;
        if (proto.OptionalRemoteStartCase != ClosuresState.OptionalRemoteStartOneofCase.None) IsRemoteStartActive = proto.RemoteStart;
    }

    private void UpdatePressuresFromProtobuf(TirePressureState proto)
    {
        if (proto.OptionalTpmsPressureFlCase != TirePressureState.OptionalTpmsPressureFlOneofCase.None) FrontLeftTire.Pressure = TeslaValues.Decimal(proto.TpmsPressureFl);
        if (proto.OptionalTpmsPressureFrCase != TirePressureState.OptionalTpmsPressureFrOneofCase.None) FrontRightTire.Pressure = TeslaValues.Decimal(proto.TpmsPressureFr);
        if (proto.OptionalTpmsPressureRlCase != TirePressureState.OptionalTpmsPressureRlOneofCase.None) RearLeftTire.Pressure = TeslaValues.Decimal(proto.TpmsPressureRl);
        if (proto.OptionalTpmsPressureRrCase != TirePressureState.OptionalTpmsPressureRrOneofCase.None) RearRightTire.Pressure = TeslaValues.Decimal(proto.TpmsPressureRr);

        if (proto.OptionalTpmsRcpFrontValueCase != TirePressureState.OptionalTpmsRcpFrontValueOneofCase.None)
        {
            var recommended = TeslaValues.Decimal(proto.TpmsRcpFrontValue);
            FrontLeftTire.RecommendedPressure = recommended;
            FrontRightTire.RecommendedPressure = recommended;
        }

        if (proto.OptionalTpmsRcpRearValueCase != TirePressureState.OptionalTpmsRcpRearValueOneofCase.None)
        {
            var recommended = TeslaValues.Decimal(proto.TpmsRcpRearValue);
            RearLeftTire.RecommendedPressure = recommended;
            RearRightTire.RecommendedPressure = recommended;
        }
    }

    private void UpdateWarningsFromProtobuf(TirePressureState proto)
    {
        if (proto.OptionalTpmsSoftWarningFlCase != TirePressureState.OptionalTpmsSoftWarningFlOneofCase.None) FrontLeftTire.HasSoftWarning = proto.TpmsSoftWarningFl;
        if (proto.OptionalTpmsSoftWarningFrCase != TirePressureState.OptionalTpmsSoftWarningFrOneofCase.None) FrontRightTire.HasSoftWarning = proto.TpmsSoftWarningFr;
        if (proto.OptionalTpmsSoftWarningRlCase != TirePressureState.OptionalTpmsSoftWarningRlOneofCase.None) RearLeftTire.HasSoftWarning = proto.TpmsSoftWarningRl;
        if (proto.OptionalTpmsSoftWarningRrCase != TirePressureState.OptionalTpmsSoftWarningRrOneofCase.None) RearRightTire.HasSoftWarning = proto.TpmsSoftWarningRr;
        if (proto.OptionalTpmsHardWarningFlCase != TirePressureState.OptionalTpmsHardWarningFlOneofCase.None) FrontLeftTire.HasHardWarning = proto.TpmsHardWarningFl;
        if (proto.OptionalTpmsHardWarningFrCase != TirePressureState.OptionalTpmsHardWarningFrOneofCase.None) FrontRightTire.HasHardWarning = proto.TpmsHardWarningFr;
        if (proto.OptionalTpmsHardWarningRlCase != TirePressureState.OptionalTpmsHardWarningRlOneofCase.None) RearLeftTire.HasHardWarning = proto.TpmsHardWarningRl;
        if (proto.OptionalTpmsHardWarningRrCase != TirePressureState.OptionalTpmsHardWarningRrOneofCase.None) RearRightTire.HasHardWarning = proto.TpmsHardWarningRr;
    }

    // The JSON has whole seconds, the snapshot has fractions of a second.
    private void UpdateLastSeenFromProtobuf(TirePressureState proto)
    {
        if (proto.TpmsLastSeenPressureTimeFl is { } frontLeft) FrontLeftTire.LastSeen = TeslaValues.FromUnixSeconds(frontLeft.Seconds);
        if (proto.TpmsLastSeenPressureTimeFr is { } frontRight) FrontRightTire.LastSeen = TeslaValues.FromUnixSeconds(frontRight.Seconds);
        if (proto.TpmsLastSeenPressureTimeRl is { } rearLeft) RearLeftTire.LastSeen = TeslaValues.FromUnixSeconds(rearLeft.Seconds);
        if (proto.TpmsLastSeenPressureTimeRr is { } rearRight) RearRightTire.LastSeen = TeslaValues.FromUnixSeconds(rearRight.Seconds);
    }
}
