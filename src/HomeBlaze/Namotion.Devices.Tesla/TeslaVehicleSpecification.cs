using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The configuration the vehicle was built with, from the vehicle configuration of the JSON. Tesla's own values are kept as text.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaVehicleSpecification : ITitleProvider, IIconProvider
{
    private readonly TeslaSectionClock _clock = new();

    public string Title => "Specification";

    public string IconName => "Info";

    [State(Position = 1)] public partial string? CarType { get; internal set; }

    [State(Position = 2)] public partial string? CarSpecialType { get; internal set; }

    [State(Position = 3)] public partial string? TrimBadging { get; internal set; }

    [State(Position = 4)] public partial string? ExteriorColor { get; internal set; }

    [State(Position = 5)] public partial string? ExteriorTrim { get; internal set; }

    [State(Position = 6)] public partial string? ExteriorTrimOverride { get; internal set; }

    [State(Position = 7)] public partial string? PaintColorOverride { get; internal set; }

    [State(Position = 8)] public partial string? InteriorTrimType { get; internal set; }

    [State(Position = 9)] public partial string? RoofColor { get; internal set; }

    [State(Position = 10)] public partial string? WheelType { get; internal set; }

    [State(Position = 11)] public partial string? SpoilerType { get; internal set; }

    [State(Position = 12)] public partial string? ThirdRowSeats { get; internal set; }

    [State(Position = 13)] public partial string? PerformancePackage { get; internal set; }

    [State(Position = 14)] public partial string? EfficiencyPackage { get; internal set; }

    [State(Position = 15)] public partial string? RearDriveUnit { get; internal set; }

    [State(Position = 16)] public partial string? DriverAssist { get; internal set; }

    [State(Position = 17)] public partial string? ChargePortType { get; internal set; }

    [State(Position = 18)] public partial string? HeadlampType { get; internal set; }

    [State(Position = 19)] public partial string? AuxiliaryParkLamps { get; internal set; }

    /// <summary>The number of rear seat heaters.</summary>
    [State(Position = 20)] public partial int? RearSeatHeaters { get; internal set; }

    [State(Position = 21)] public partial int? RearSeatType { get; internal set; }

    [State(Position = 22)] public partial int? SeatType { get; internal set; }

    [State(Position = 23)] public partial int? BadgeVersion { get; internal set; }

    [State(Position = 24)] public partial int? KeyVersion { get; internal set; }

    [State(IsDiscrete = true, Position = 25)] public partial bool? HasSunRoof { get; internal set; }

    [State(IsDiscrete = true, Position = 26)] public partial bool? HasWheelCaps { get; internal set; }

    [State(IsDiscrete = true, Position = 27)] public partial bool? HasSeatCooling { get; internal set; }

    [State(IsDiscrete = true, Position = 28)] public partial bool? HasAirSuspension { get; internal set; }

    [State(IsDiscrete = true, Position = 29)] public partial bool? HasLudicrousMode { get; internal set; }

    [State(IsDiscrete = true, Position = 30)] public partial bool? HasMotorizedChargePort { get; internal set; }

    /// <summary>Whether the vehicle has a power liftgate.</summary>
    [State(IsDiscrete = true, Position = 31)] public partial bool? HasPowerLiftgate { get; internal set; }

    /// <summary>Whether the vehicle has a speaker that warns pedestrians.</summary>
    [State(IsDiscrete = true, Position = 32)] public partial bool? HasPedestrianWarningSpeaker { get; internal set; }

    /// <summary>Whether the steering wheel is on the right.</summary>
    [State(IsDiscrete = true, Position = 33)] public partial bool? IsRightHandDrive { get; internal set; }

    [State(IsDiscrete = true, Position = 34)] public partial bool? IsEuVehicle { get; internal set; }

    /// <summary>Whether the vehicle follows the ECE regulations.</summary>
    [State(IsDiscrete = true, Position = 35)] public partial bool? HasEceRestrictions { get; internal set; }

    /// <summary>Whether the charge limit defaults to the maximum.</summary>
    [State(IsDiscrete = true, Position = 36)] public partial bool? DefaultsChargeToMaximum { get; internal set; }

    /// <summary>Whether the vehicle shows its range in its badge.</summary>
    [State(IsDiscrete = true, Position = 37)] public partial bool? UsesRangeBadging { get; internal set; }

    [State(IsDiscrete = true, Position = 38)] public partial bool? CanAcceptNavigationRequests { get; internal set; }

    [State(IsDiscrete = true, Position = 39)] public partial bool? CanActuateTrunks { get; internal set; }

    [State(IsDiscrete = true, Position = 40)] public partial bool? IsDashcamClipSaveSupported { get; internal set; }

    [State(IsDiscrete = true, Position = 41)] public partial bool? IsWebcamSupported { get; internal set; }

    [State(IsDiscrete = true, Position = 42)] public partial bool? IsWebcamSelfieSupported { get; internal set; }

    /// <summary>Whether the webcam sees only the interior.</summary>
    [State(IsDiscrete = true, Position = 43)] public partial bool? IsWebcamInteriorOnly { get; internal set; }

    [State(IsDiscrete = true, Position = 44)] public partial bool? IsSentryPreviewSupported { get; internal set; }

    [State(IsDiscrete = true, Position = 45)] public partial bool? IsQrPairingSupported { get; internal set; }

    [State(IsDiscrete = true, Position = 46)] public partial bool? IsCabinOverheatProtectionTemperatureSupported { get; internal set; }

    /// <summary>The offset of the time zone of the vehicle from UTC.</summary>
    [State(Position = 47)] public partial TimeSpan? UtcOffset { get; internal set; }

    internal TeslaVehicleSpecification()
    {
        CarType = null;
        CarSpecialType = null;
        TrimBadging = null;
        ExteriorColor = null;
        ExteriorTrim = null;
        ExteriorTrimOverride = null;
        PaintColorOverride = null;
        InteriorTrimType = null;
        RoofColor = null;
        WheelType = null;
        SpoilerType = null;
        ThirdRowSeats = null;
        PerformancePackage = null;
        EfficiencyPackage = null;
        RearDriveUnit = null;
        DriverAssist = null;
        ChargePortType = null;
        HeadlampType = null;
        AuxiliaryParkLamps = null;
        RearSeatHeaters = null;
        RearSeatType = null;
        SeatType = null;
        BadgeVersion = null;
        KeyVersion = null;
        HasSunRoof = null;
        HasWheelCaps = null;
        HasSeatCooling = null;
        HasAirSuspension = null;
        HasLudicrousMode = null;
        HasMotorizedChargePort = null;
        HasPowerLiftgate = null;
        HasPedestrianWarningSpeaker = null;
        IsRightHandDrive = null;
        IsEuVehicle = null;
        HasEceRestrictions = null;
        DefaultsChargeToMaximum = null;
        UsesRangeBadging = null;
        CanAcceptNavigationRequests = null;
        CanActuateTrunks = null;
        IsDashcamClipSaveSupported = null;
        IsWebcamSupported = null;
        IsWebcamSelfieSupported = null;
        IsWebcamInteriorOnly = null;
        IsSentryPreviewSupported = null;
        IsQrPairingSupported = null;
        IsCabinOverheatProtectionTemperatureSupported = null;
        UtcOffset = null;
    }

    internal void Update(TeslaVehicleConfigDto dto)
    {
        if (!_clock.TryAdvance(TeslaValues.FromUnixMilliseconds(dto.Timestamp)))
        {
            return;
        }

        UpdateAppearanceFromJson(dto);
        UpdateEquipmentFromJson(dto);
        UpdateHardwareFromJson(dto);
        UpdateFlagsFromJson(dto);
    }

    private void UpdateAppearanceFromJson(TeslaVehicleConfigDto dto)
    {
        CarType = TeslaValues.Text(dto.CarType);
        CarSpecialType = TeslaValues.Text(dto.CarSpecialType);
        TrimBadging = TeslaValues.Text(dto.TrimBadging);
        ExteriorColor = TeslaValues.Text(dto.ExteriorColor);
        ExteriorTrim = TeslaValues.Text(dto.ExteriorTrim);
        ExteriorTrimOverride = TeslaValues.Text(dto.ExteriorTrimOverride);
        PaintColorOverride = TeslaValues.Text(dto.PaintColorOverride);
        InteriorTrimType = TeslaValues.Text(dto.InteriorTrimType);
        RoofColor = TeslaValues.Text(dto.RoofColor);
        WheelType = TeslaValues.Text(dto.WheelType);
        SpoilerType = TeslaValues.Text(dto.SpoilerType);
    }

    private void UpdateEquipmentFromJson(TeslaVehicleConfigDto dto)
    {
        ThirdRowSeats = TeslaValues.Text(dto.ThirdRowSeats);
        PerformancePackage = TeslaValues.Text(dto.PerformancePackage);
        EfficiencyPackage = TeslaValues.Text(dto.EfficiencyPackage);
        RearDriveUnit = TeslaValues.Text(dto.RearDriveUnit);
        DriverAssist = TeslaValues.Text(dto.DriverAssist);
        ChargePortType = TeslaValues.Text(dto.ChargePortType);
        HeadlampType = TeslaValues.Text(dto.HeadlampType);
        AuxiliaryParkLamps = TeslaValues.Text(dto.AuxParkLamps);
        RearSeatHeaters = dto.RearSeatHeaters;
        RearSeatType = dto.RearSeatType;
        SeatType = dto.SeatType;
    }

    private void UpdateHardwareFromJson(TeslaVehicleConfigDto dto)
    {
        BadgeVersion = dto.BadgeVersion;
        KeyVersion = dto.KeyVersion;
        HasSunRoof = dto.SunRoofInstalled is { } sunRoof ? sunRoof > 0 : null;
        UtcOffset = TeslaValues.FromSeconds(dto.UtcOffset);
        HasWheelCaps = dto.WheelCapsOn;
        HasSeatCooling = dto.HasSeatCooling;
        HasAirSuspension = dto.HasAirSuspension;
        HasLudicrousMode = dto.HasLudicrousMode;
        HasMotorizedChargePort = dto.MotorizedChargePort;
        HasPowerLiftgate = dto.Plg;
        HasPedestrianWarningSpeaker = dto.Pws;
        IsRightHandDrive = dto.Rhd;
        IsEuVehicle = dto.EuVehicle;
    }

    private void UpdateFlagsFromJson(TeslaVehicleConfigDto dto)
    {
        HasEceRestrictions = dto.EceRestrictions;
        DefaultsChargeToMaximum = dto.DefaultChargeToMax;
        UsesRangeBadging = dto.UseRangeBadging;
        CanAcceptNavigationRequests = dto.CanAcceptNavigationRequests;
        CanActuateTrunks = dto.CanActuateTrunks;
        IsDashcamClipSaveSupported = dto.DashcamClipSaveSupported;
        IsWebcamSupported = dto.WebcamSupported;
        IsWebcamSelfieSupported = dto.WebcamSelfieSupported;
        IsWebcamInteriorOnly = dto.WebcamInteriorOnly;
        IsSentryPreviewSupported = dto.SentryPreviewSupported;
        IsQrPairingSupported = dto.SupportsQrPairing;
        IsCabinOverheatProtectionTemperatureSupported = dto.CopUserSetTempSupported;
    }
}
