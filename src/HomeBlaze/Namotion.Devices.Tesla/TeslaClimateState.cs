using System.ComponentModel;
using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Vehicles;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The climate state of a Tesla vehicle.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaClimateState : IVehicleClimateState, ITitleProvider, IIconProvider
{
    private readonly ILogger _logger;
    private readonly TeslaSectionClock _clock = new();

    public string Title => "Climate";

    public string IconName => "Thermostat";

    [Category("")]
    [State(IsDiscrete = true, Position = 1)] public partial bool? IsClimateOn { get; internal set; }

    [State(IsDiscrete = true, Position = 2)] public partial bool? IsAutoConditioningOn { get; internal set; }

    [State(IsDiscrete = true, Position = 3)] public partial bool? IsPreconditioning { get; internal set; }

    [Category("")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 4)] public partial decimal? InsideTemperature { get; internal set; }

    [Category("")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 5)] public partial decimal? OutsideTemperature { get; internal set; }

    /// <summary>The target temperature on the driver side.</summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 6)] public partial decimal? DriverTemperatureSetting { get; internal set; }

    /// <summary>The target temperature on the passenger side.</summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 7)] public partial decimal? PassengerTemperatureSetting { get; internal set; }

    /// <summary>The lowest temperature that can be set.</summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 8)] public partial decimal? MinimumTemperatureSetting { get; internal set; }

    /// <summary>The highest temperature that can be set.</summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 9)] public partial decimal? MaximumTemperatureSetting { get; internal set; }

    /// <summary>The fan speed level.</summary>
    [State(Position = 10)] public partial int? FanStatus { get; internal set; }

    /// <summary>Tesla's indicator in which direction the left side temperature is regulated.</summary>
    [State(Position = 11)] public partial int? LeftTemperatureDirection { get; internal set; }

    /// <summary>Tesla's indicator in which direction the right side temperature is regulated.</summary>
    [State(Position = 12)] public partial int? RightTemperatureDirection { get; internal set; }

    [State(IsDiscrete = true, Position = 13)] public partial TeslaHvacAutoRequest? HvacAutoRequest { get; internal set; }

    [State(IsDiscrete = true, Position = 14)] public partial TeslaClimateKeeperMode? ClimateKeeperMode { get; internal set; }

    /// <summary>The defrost mode: 0 off, 1 normal, 2 max.</summary>
    [State(Position = 15)] public partial int? DefrostMode { get; internal set; }

    [State(IsDiscrete = true, Position = 16)] public partial bool? IsFrontDefrosterOn { get; internal set; }

    [State(IsDiscrete = true, Position = 17)] public partial bool? IsRearDefrosterOn { get; internal set; }

    [State(IsDiscrete = true, Position = 18)] public partial bool? IsSideMirrorHeaterOn { get; internal set; }

    [State(IsDiscrete = true, Position = 19)] public partial bool? IsWiperBladeHeaterOn { get; internal set; }

    [State(IsDiscrete = true, Position = 20)] public partial bool? IsBatteryHeaterOn { get; internal set; }

    /// <summary>Whether the battery heater is requested but the charger power does not suffice.</summary>
    [State(IsDiscrete = true, Position = 21)] public partial bool? IsBatteryHeaterWithoutPower { get; internal set; }

    [State(IsDiscrete = true, Position = 22)] public partial bool? IsRemoteHeaterControlEnabled { get; internal set; }

    [State(IsDiscrete = true, Position = 23)] public partial bool? IsCabinOverheatProtectionAllowed { get; internal set; }

    [State(IsDiscrete = true, Position = 24)] public partial bool? IsFanOnlyCabinOverheatProtectionSupported { get; internal set; }

    [State(IsDiscrete = true, Position = 25)] public partial TeslaCabinOverheatProtection? CabinOverheatProtection { get; internal set; }

    /// <summary>Whether the cabin overheat protection is actively cooling.</summary>
    [State(IsDiscrete = true, Position = 26)] public partial bool? IsCabinOverheatProtectionCooling { get; internal set; }

    /// <summary>The cabin temperature at which the cabin overheat protection starts.</summary>
    [State(IsDiscrete = true, Position = 27)] public partial TeslaCabinOverheatProtectionTemperature? CabinOverheatProtectionTemperature { get; internal set; }

    [State(Position = 28)] public partial TeslaSeatHeaters SeatHeaters { get; internal set; }

    internal TeslaClimateState(ILogger logger)
    {
        _logger = logger;
        IsClimateOn = null;
        IsAutoConditioningOn = null;
        IsPreconditioning = null;
        InsideTemperature = null;
        OutsideTemperature = null;
        DriverTemperatureSetting = null;
        PassengerTemperatureSetting = null;
        MinimumTemperatureSetting = null;
        MaximumTemperatureSetting = null;
        FanStatus = null;
        LeftTemperatureDirection = null;
        RightTemperatureDirection = null;
        HvacAutoRequest = null;
        ClimateKeeperMode = null;
        DefrostMode = null;
        IsFrontDefrosterOn = null;
        IsRearDefrosterOn = null;
        IsSideMirrorHeaterOn = null;
        IsWiperBladeHeaterOn = null;
        IsBatteryHeaterOn = null;
        IsBatteryHeaterWithoutPower = null;
        IsRemoteHeaterControlEnabled = null;
        IsCabinOverheatProtectionAllowed = null;
        IsFanOnlyCabinOverheatProtectionSupported = null;
        CabinOverheatProtection = null;
        IsCabinOverheatProtectionCooling = null;
        CabinOverheatProtectionTemperature = null;
        SeatHeaters = new TeslaSeatHeaters();
    }

    internal void Update(TeslaClimateStateDto dto)
    {
        if (!_clock.TryAdvance(TeslaValues.FromUnixMilliseconds(dto.Timestamp)))
        {
            return;
        }

        UpdateOperationFromJson(dto);
        UpdateTemperaturesFromJson(dto);
        UpdateHeatersFromJson(dto);
        UpdateCabinOverheatProtectionFromJson(dto);
        UpdateSeatHeatersFromJson(dto);
    }

    internal void Update(ClimateState proto)
    {
        if (!_clock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateOperationFromProtobuf(proto);
        UpdateModesFromProtobuf(proto);
        UpdateTemperaturesFromProtobuf(proto);
        UpdateHeatersFromProtobuf(proto);
        UpdateCabinOverheatProtectionFromProtobuf(proto);
        UpdateSeatHeatersFromProtobuf(proto);
        UpdateSteeringWheelHeaterFromProtobuf(proto);
    }

    private void UpdateOperationFromJson(TeslaClimateStateDto dto)
    {
        IsClimateOn = dto.IsClimateOn;
        IsAutoConditioningOn = dto.IsAutoConditioningOn;
        IsPreconditioning = dto.IsPreconditioning;
        FanStatus = dto.FanStatus;
        HvacAutoRequest = TeslaValues.Enum<TeslaHvacAutoRequest>(dto.HvacAutoRequest, _logger);
        ClimateKeeperMode = TeslaValues.Enum<TeslaClimateKeeperMode>(dto.ClimateKeeperMode, _logger);
        DefrostMode = dto.DefrostMode;
    }

    private void UpdateTemperaturesFromJson(TeslaClimateStateDto dto)
    {
        InsideTemperature = dto.InsideTemp;
        OutsideTemperature = dto.OutsideTemp;
        DriverTemperatureSetting = dto.DriverTempSetting;
        PassengerTemperatureSetting = dto.PassengerTempSetting;
        MinimumTemperatureSetting = dto.MinAvailTemp;
        MaximumTemperatureSetting = dto.MaxAvailTemp;
        LeftTemperatureDirection = dto.LeftTempDirection;
        RightTemperatureDirection = dto.RightTempDirection;
    }

    private void UpdateHeatersFromJson(TeslaClimateStateDto dto)
    {
        IsFrontDefrosterOn = dto.IsFrontDefrosterOn;
        IsRearDefrosterOn = dto.IsRearDefrosterOn;
        IsSideMirrorHeaterOn = dto.SideMirrorHeaters;
        IsWiperBladeHeaterOn = dto.WiperBladeHeater;
        IsBatteryHeaterOn = dto.BatteryHeater;
        IsBatteryHeaterWithoutPower = dto.BatteryHeaterNoPower;
        IsRemoteHeaterControlEnabled = dto.RemoteHeaterControlEnabled;
    }

    private void UpdateCabinOverheatProtectionFromJson(TeslaClimateStateDto dto)
    {
        IsCabinOverheatProtectionAllowed = dto.AllowCabinOverheatProtection;
        IsFanOnlyCabinOverheatProtectionSupported = dto.SupportsFanOnlyCabinOverheatProtection;
        CabinOverheatProtection = TeslaValues.Enum<TeslaCabinOverheatProtection>(dto.CabinOverheatProtection, _logger);
        IsCabinOverheatProtectionCooling = dto.CabinOverheatProtectionActivelyCooling;
        CabinOverheatProtectionTemperature = TeslaValues.Enum<TeslaCabinOverheatProtectionTemperature>(dto.CopActivationTemperature, _logger);
    }

    private void UpdateSeatHeatersFromJson(TeslaClimateStateDto dto)
    {
        SeatHeaters.FrontLeftLevel = dto.SeatHeaterLeft;
        SeatHeaters.FrontRightLevel = dto.SeatHeaterRight;
        SeatHeaters.RearLeftLevel = dto.SeatHeaterRearLeft;
        SeatHeaters.RearCenterLevel = dto.SeatHeaterRearCenter;
        SeatHeaters.RearRightLevel = dto.SeatHeaterRearRight;
        SeatHeaters.IsAutoSeatClimateFrontLeftOn = dto.AutoSeatClimateLeft;
        SeatHeaters.IsAutoSeatClimateFrontRightOn = dto.AutoSeatClimateRight;
        SeatHeaters.IsSteeringWheelHeaterOn = dto.SteeringWheelHeater;
        SeatHeaters.SteeringWheelHeatLevel = dto.SteeringWheelHeatLevel;
        SeatHeaters.IsAutoSteeringWheelHeatOn = dto.AutoSteeringWheelHeat;
    }

    private void UpdateOperationFromProtobuf(ClimateState proto)
    {
        if (proto.OptionalIsClimateOnCase != ClimateState.OptionalIsClimateOnOneofCase.None) IsClimateOn = proto.IsClimateOn;
        if (proto.OptionalIsAutoConditioningOnCase != ClimateState.OptionalIsAutoConditioningOnOneofCase.None) IsAutoConditioningOn = proto.IsAutoConditioningOn;
        if (proto.OptionalIsPreconditioningCase != ClimateState.OptionalIsPreconditioningOneofCase.None) IsPreconditioning = proto.IsPreconditioning;
        if (proto.OptionalFanStatusCase != ClimateState.OptionalFanStatusOneofCase.None) FanStatus = proto.FanStatus;
    }

    private void UpdateModesFromProtobuf(ClimateState proto)
    {
        if (proto.OptionalHvacAutoRequestCase != ClimateState.OptionalHvacAutoRequestOneofCase.None && TeslaProtoValues.HvacAutoRequest(TeslaProtoValues.Checked(proto.HvacAutoRequest, _logger)) is { } hvacAutoRequest) HvacAutoRequest = hvacAutoRequest;
        if (proto.ClimateKeeperMode is { } climateKeeperMode && TeslaProtoValues.ClimateKeeperMode(climateKeeperMode) is { } mappedClimateKeeperMode) ClimateKeeperMode = mappedClimateKeeperMode;
        if (proto.DefrostMode is { } defrostMode && TeslaProtoValues.DefrostMode(defrostMode) is { } mappedDefrostMode) DefrostMode = mappedDefrostMode;
    }

    private void UpdateTemperaturesFromProtobuf(ClimateState proto)
    {
        if (proto.OptionalInsideTempCelsiusCase != ClimateState.OptionalInsideTempCelsiusOneofCase.None) InsideTemperature = TeslaValues.Decimal(proto.InsideTempCelsius);
        if (proto.OptionalOutsideTempCelsiusCase != ClimateState.OptionalOutsideTempCelsiusOneofCase.None) OutsideTemperature = TeslaValues.Decimal(proto.OutsideTempCelsius);
        if (proto.OptionalDriverTempSettingCase != ClimateState.OptionalDriverTempSettingOneofCase.None) DriverTemperatureSetting = TeslaValues.Decimal(proto.DriverTempSetting);
        if (proto.OptionalPassengerTempSettingCase != ClimateState.OptionalPassengerTempSettingOneofCase.None) PassengerTemperatureSetting = TeslaValues.Decimal(proto.PassengerTempSetting);
        if (proto.OptionalMinAvailTempCelsiusCase != ClimateState.OptionalMinAvailTempCelsiusOneofCase.None) MinimumTemperatureSetting = TeslaValues.Decimal(proto.MinAvailTempCelsius);
        if (proto.OptionalMaxAvailTempCelsiusCase != ClimateState.OptionalMaxAvailTempCelsiusOneofCase.None) MaximumTemperatureSetting = TeslaValues.Decimal(proto.MaxAvailTempCelsius);
        if (proto.OptionalLeftTempDirectionCase != ClimateState.OptionalLeftTempDirectionOneofCase.None) LeftTemperatureDirection = proto.LeftTempDirection;
        if (proto.OptionalRightTempDirectionCase != ClimateState.OptionalRightTempDirectionOneofCase.None) RightTemperatureDirection = proto.RightTempDirection;
    }

    private void UpdateHeatersFromProtobuf(ClimateState proto)
    {
        if (proto.OptionalIsFrontDefrosterOnCase != ClimateState.OptionalIsFrontDefrosterOnOneofCase.None) IsFrontDefrosterOn = proto.IsFrontDefrosterOn;
        if (proto.OptionalIsRearDefrosterOnCase != ClimateState.OptionalIsRearDefrosterOnOneofCase.None) IsRearDefrosterOn = proto.IsRearDefrosterOn;
        if (proto.OptionalSideMirrorHeatersCase != ClimateState.OptionalSideMirrorHeatersOneofCase.None) IsSideMirrorHeaterOn = proto.SideMirrorHeaters;
        if (proto.OptionalWiperBladeHeaterCase != ClimateState.OptionalWiperBladeHeaterOneofCase.None) IsWiperBladeHeaterOn = proto.WiperBladeHeater;
        if (proto.OptionalBatteryHeaterCase != ClimateState.OptionalBatteryHeaterOneofCase.None) IsBatteryHeaterOn = proto.BatteryHeater;
        if (proto.OptionalBatteryHeaterNoPowerCase != ClimateState.OptionalBatteryHeaterNoPowerOneofCase.None) IsBatteryHeaterWithoutPower = proto.BatteryHeaterNoPower;
        if (proto.OptionalRemoteHeaterControlEnabledCase != ClimateState.OptionalRemoteHeaterControlEnabledOneofCase.None) IsRemoteHeaterControlEnabled = proto.RemoteHeaterControlEnabled;
    }

    private void UpdateCabinOverheatProtectionFromProtobuf(ClimateState proto)
    {
        if (proto.OptionalAllowCabinOverheatProtectionCase != ClimateState.OptionalAllowCabinOverheatProtectionOneofCase.None) IsCabinOverheatProtectionAllowed = proto.AllowCabinOverheatProtection;
        if (proto.OptionalSupportsFanOnlyCabinOverheatProtectionCase != ClimateState.OptionalSupportsFanOnlyCabinOverheatProtectionOneofCase.None) IsFanOnlyCabinOverheatProtectionSupported = proto.SupportsFanOnlyCabinOverheatProtection;
        if (proto.OptionalCabinOverheatProtectionCase != ClimateState.OptionalCabinOverheatProtectionOneofCase.None && TeslaProtoValues.CabinOverheatProtection(TeslaProtoValues.Checked(proto.CabinOverheatProtection, _logger)) is { } protection) CabinOverheatProtection = protection;
        if (proto.OptionalCabinOverheatProtectionActivelyCoolingCase != ClimateState.OptionalCabinOverheatProtectionActivelyCoolingOneofCase.None) IsCabinOverheatProtectionCooling = proto.CabinOverheatProtectionActivelyCooling;
        if (proto.OptionalCopActivationTemperatureCase != ClimateState.OptionalCopActivationTemperatureOneofCase.None && TeslaProtoValues.CabinOverheatProtectionTemperature(TeslaProtoValues.Checked(proto.CopActivationTemperature, _logger)) is { } temperature) CabinOverheatProtectionTemperature = temperature;
    }

    private void UpdateSeatHeatersFromProtobuf(ClimateState proto)
    {
        if (proto.OptionalSeatHeaterLeftCase != ClimateState.OptionalSeatHeaterLeftOneofCase.None) SeatHeaters.FrontLeftLevel = proto.SeatHeaterLeft;
        if (proto.OptionalSeatHeaterRightCase != ClimateState.OptionalSeatHeaterRightOneofCase.None) SeatHeaters.FrontRightLevel = proto.SeatHeaterRight;
        if (proto.OptionalSeatHeaterRearLeftCase != ClimateState.OptionalSeatHeaterRearLeftOneofCase.None) SeatHeaters.RearLeftLevel = proto.SeatHeaterRearLeft;
        if (proto.OptionalSeatHeaterRearCenterCase != ClimateState.OptionalSeatHeaterRearCenterOneofCase.None) SeatHeaters.RearCenterLevel = proto.SeatHeaterRearCenter;
        if (proto.OptionalSeatHeaterRearRightCase != ClimateState.OptionalSeatHeaterRearRightOneofCase.None) SeatHeaters.RearRightLevel = proto.SeatHeaterRearRight;
        if (proto.OptionalAutoSeatClimateLeftCase != ClimateState.OptionalAutoSeatClimateLeftOneofCase.None) SeatHeaters.IsAutoSeatClimateFrontLeftOn = proto.AutoSeatClimateLeft;
        if (proto.OptionalAutoSeatClimateRightCase != ClimateState.OptionalAutoSeatClimateRightOneofCase.None) SeatHeaters.IsAutoSeatClimateFrontRightOn = proto.AutoSeatClimateRight;
    }

    private void UpdateSteeringWheelHeaterFromProtobuf(ClimateState proto)
    {
        if (proto.OptionalSteeringWheelHeaterCase != ClimateState.OptionalSteeringWheelHeaterOneofCase.None) SeatHeaters.IsSteeringWheelHeaterOn = proto.SteeringWheelHeater;
        if (proto.OptionalSteeringWheelHeatLevelCase != ClimateState.OptionalSteeringWheelHeatLevelOneofCase.None && TeslaProtoValues.SteeringWheelHeatLevel(TeslaProtoValues.Checked(proto.SteeringWheelHeatLevel, _logger)) is { } heatLevel) SeatHeaters.SteeringWheelHeatLevel = heatLevel;
        if (proto.OptionalAutoSteeringWheelHeatCase != ClimateState.OptionalAutoSteeringWheelHeatOneofCase.None) SeatHeaters.IsAutoSteeringWheelHeatOn = proto.AutoSteeringWheelHeat;
    }
}
