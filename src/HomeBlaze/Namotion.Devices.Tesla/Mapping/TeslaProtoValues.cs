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
}
