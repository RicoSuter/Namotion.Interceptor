using CarServer;

namespace Namotion.Devices.Tesla.Mapping;

/// <summary>
/// Maps the protobuf messages of the cached snapshot to the values the JSON path produces for the same state.
/// SNA ("signal not available") and values this library does not know become null.
/// </summary>
internal static class TeslaProtoValues
{
    private const string ChargePortColorPrefix = "ChargePortColor";

    public static TeslaChargingState ChargingState(ChargeState.Types.ChargingState state) => state.TypeCase switch
    {
        ChargeState.Types.ChargingState.TypeOneofCase.Disconnected => TeslaChargingState.Disconnected,
        ChargeState.Types.ChargingState.TypeOneofCase.NoPower => TeslaChargingState.NoPower,
        ChargeState.Types.ChargingState.TypeOneofCase.Starting => TeslaChargingState.Starting,
        ChargeState.Types.ChargingState.TypeOneofCase.Charging => TeslaChargingState.Charging,
        ChargeState.Types.ChargingState.TypeOneofCase.Complete => TeslaChargingState.Complete,
        ChargeState.Types.ChargingState.TypeOneofCase.Stopped => TeslaChargingState.Stopped,
        ChargeState.Types.ChargingState.TypeOneofCase.Calibrating => TeslaChargingState.Calibrating,
        _ => TeslaChargingState.Unknown
    };

    public static string? ChargeCable(ChargeState.Types.CableType cable) => cable.TypeCase switch
    {
        ChargeState.Types.CableType.TypeOneofCase.IEC => "IEC",
        ChargeState.Types.CableType.TypeOneofCase.SAE => "SAE",
        ChargeState.Types.CableType.TypeOneofCase.GBAC => "GB_AC",
        ChargeState.Types.CableType.TypeOneofCase.GBDC => "GB_DC",
        _ => null
    };

    public static string? FastChargerType(ChargeState.Types.ChargerType type) => type.TypeCase switch
    {
        ChargeState.Types.ChargerType.TypeOneofCase.Supercharger => "Supercharger",
        ChargeState.Types.ChargerType.TypeOneofCase.Tesla => "Tesla",
        ChargeState.Types.ChargerType.TypeOneofCase.Chademo => "CHAdeMO",
        ChargeState.Types.ChargerType.TypeOneofCase.Gb => "GB",
        ChargeState.Types.ChargerType.TypeOneofCase.ACSingleWireCAN => "ACSingleWireCAN",
        ChargeState.Types.ChargerType.TypeOneofCase.MCSingleWireCAN => "MCSingleWireCAN",
        ChargeState.Types.ChargerType.TypeOneofCase.Combo => "Combo",
        ChargeState.Types.ChargerType.TypeOneofCase.Other => "Other",
        _ => null
    };

    public static string? FastChargerBrand(ChargeState.Types.ChargerBrand brand) =>
        brand.TypeCase == ChargeState.Types.ChargerBrand.TypeOneofCase.Tesla ? "Tesla" : null;

    public static TeslaChargePortLatch? ChargePortLatch(ChargePortLatchState latch) => latch.TypeCase switch
    {
        ChargePortLatchState.TypeOneofCase.Engaged => TeslaChargePortLatch.Engaged,
        ChargePortLatchState.TypeOneofCase.Disengaged => TeslaChargePortLatch.Disengaged,
        ChargePortLatchState.TypeOneofCase.Blocking => TeslaChargePortLatch.Blocking,
        _ => null
    };

    public static TeslaScheduledChargingMode? ScheduledChargingMode(ChargeState.Types.ScheduledChargingMode mode) => mode switch
    {
        ChargeState.Types.ScheduledChargingMode.Off => TeslaScheduledChargingMode.Off,
        ChargeState.Types.ScheduledChargingMode.StartAt => TeslaScheduledChargingMode.StartAt,
        ChargeState.Types.ScheduledChargingMode.DepartBy => TeslaScheduledChargingMode.DepartBy,
        _ => null
    };

    /// <summary>Returns the name of the color without the prefix, such as "FlashingGreen", as the JSON path does.</summary>
    public static string? ChargePortColor(ChargeState.Types.ChargePortColor_E color) =>
        Enum.IsDefined(color) ? color.ToString()[ChargePortColorPrefix.Length..] : null;

    public static TeslaScheduleDays? Days(OffPeakChargingTimes times) => times.TimesCase switch
    {
        OffPeakChargingTimes.TimesOneofCase.AllWeek => TeslaScheduleDays.AllWeek,
        OffPeakChargingTimes.TimesOneofCase.Weekdays => TeslaScheduleDays.Weekdays,
        _ => null
    };

    public static TeslaScheduleDays? Days(PreconditioningTimes times) => times.TimesCase switch
    {
        PreconditioningTimes.TimesOneofCase.AllWeek => TeslaScheduleDays.AllWeek,
        PreconditioningTimes.TimesOneofCase.Weekdays => TeslaScheduleDays.Weekdays,
        _ => null
    };

    public static TeslaCabinOverheatProtection? CabinOverheatProtection(ClimateState.Types.CabinOverheatProtection_E protection) => protection switch
    {
        ClimateState.Types.CabinOverheatProtection_E.CabinOverheatProtectionOff => TeslaCabinOverheatProtection.Off,
        ClimateState.Types.CabinOverheatProtection_E.CabinOverheatProtectionOn => TeslaCabinOverheatProtection.On,
        ClimateState.Types.CabinOverheatProtection_E.CabinOverheatProtectionFanOnly => TeslaCabinOverheatProtection.FanOnly,
        _ => null
    };

    public static TeslaCabinOverheatProtectionTemperature? CabinOverheatProtectionTemperature(ClimateState.Types.CopActivationTemp temperature) => temperature switch
    {
        ClimateState.Types.CopActivationTemp.Low => TeslaCabinOverheatProtectionTemperature.Low,
        ClimateState.Types.CopActivationTemp.Medium => TeslaCabinOverheatProtectionTemperature.Medium,
        ClimateState.Types.CopActivationTemp.High => TeslaCabinOverheatProtectionTemperature.High,
        _ => null
    };

    /// <summary>Tesla calls the camp mode "party" in the snapshot and "camp" in the JSON.</summary>
    public static TeslaClimateKeeperMode? ClimateKeeperMode(ClimateState.Types.ClimateKeeperMode mode) => mode.TypeCase switch
    {
        ClimateState.Types.ClimateKeeperMode.TypeOneofCase.Off => TeslaClimateKeeperMode.Off,
        ClimateState.Types.ClimateKeeperMode.TypeOneofCase.On => TeslaClimateKeeperMode.On,
        ClimateState.Types.ClimateKeeperMode.TypeOneofCase.Dog => TeslaClimateKeeperMode.Dog,
        ClimateState.Types.ClimateKeeperMode.TypeOneofCase.Party => TeslaClimateKeeperMode.Camp,
        _ => null
    };

    public static TeslaHvacAutoRequest? HvacAutoRequest(ClimateState.Types.HvacAutoRequest request) => request switch
    {
        ClimateState.Types.HvacAutoRequest.On => TeslaHvacAutoRequest.On,
        ClimateState.Types.HvacAutoRequest.Override => TeslaHvacAutoRequest.Override,
        _ => null
    };

    /// <summary>Returns the JSON number: 0 off, 1 normal, 2 max.</summary>
    public static int? DefrostMode(ClimateState.Types.DefrostMode mode) => mode.TypeCase switch
    {
        ClimateState.Types.DefrostMode.TypeOneofCase.Off => 0,
        ClimateState.Types.DefrostMode.TypeOneofCase.Normal => 1,
        ClimateState.Types.DefrostMode.TypeOneofCase.Max => 2,
        _ => null
    };

    /// <summary>Returns the JSON number: 0 off, 1 low, 2 high.</summary>
    public static int? SteeringWheelHeatLevel(StwHeatLevel level) => level switch
    {
        StwHeatLevel.Off => 0,
        StwHeatLevel.Low => 1,
        StwHeatLevel.High => 2,
        _ => null
    };

    public static TeslaShiftState? ShiftState(CarServer.ShiftState state) => state.TypeCase switch
    {
        CarServer.ShiftState.TypeOneofCase.P => TeslaShiftState.Park,
        CarServer.ShiftState.TypeOneofCase.R => TeslaShiftState.Reverse,
        CarServer.ShiftState.TypeOneofCase.N => TeslaShiftState.Neutral,
        CarServer.ShiftState.TypeOneofCase.D => TeslaShiftState.Drive,
        _ => null
    };

    public static TeslaGpsCoordinateType? CoordinateType(LocationState.Types.GPSCoordinateType type) => type.TypeCase switch
    {
        LocationState.Types.GPSCoordinateType.TypeOneofCase.WGS => TeslaGpsCoordinateType.Wgs,
        LocationState.Types.GPSCoordinateType.TypeOneofCase.GCJ => TeslaGpsCoordinateType.Gcj,
        _ => null
    };
}
