using System.ComponentModel;
using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The charging state of a Tesla vehicle.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaChargeState : ITitleProvider, IIconProvider
{
    private const decimal MinimumChargerVoltage = 2m;

    private readonly ILogger _logger;
    private readonly TeslaSectionClock _clock = new();

    public string Title => "Charge";

    public string IconName => "EvStation";

    [State(IsDiscrete = true, Position = 1)] public partial TeslaChargingState? ChargingState { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 2)] public partial decimal? BatteryLevel { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 3)] public partial decimal? UsableBatteryLevel { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 4)] public partial decimal? ChargeLimit { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 5)] public partial decimal? MinimumChargeLimit { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 6)] public partial decimal? MaximumChargeLimit { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 7)] public partial decimal? StandardChargeLimit { get; internal set; }
    [State(Unit = StateUnit.Kilometer, Position = 8)] public partial decimal? Range { get; internal set; }
    [State(Unit = StateUnit.Kilometer, Position = 9)] public partial decimal? IdealRange { get; internal set; }
    [State(Unit = StateUnit.Kilometer, IsEstimated = true, Position = 10)] public partial decimal? EstimatedRange { get; internal set; }
    [State(Unit = StateUnit.Watt, Position = 11)] public partial decimal? ChargingPower { get; internal set; }
    [State(Unit = StateUnit.Ampere, Position = 12)] public partial decimal? ChargingCurrent { get; internal set; }
    [State(Unit = StateUnit.Ampere, Position = 13)] public partial decimal? RequestedChargingCurrent { get; internal set; }
    [State(Unit = StateUnit.Ampere, Position = 14)] public partial decimal? MaximumRequestedChargingCurrent { get; internal set; }
    [State(Unit = StateUnit.Ampere, Position = 15)] public partial decimal? PilotCurrent { get; internal set; }
    [State(Unit = StateUnit.Volt, Position = 16)] public partial decimal? ChargerVoltage { get; internal set; }
    [State(Position = 17)] public partial int? ChargerPhases { get; internal set; }
    [State(Unit = StateUnit.WattHour, Position = 18)] public partial decimal? AddedEnergy { get; internal set; }
    [State(Unit = StateUnit.Kilometer, Position = 19)] public partial decimal? AddedRange { get; internal set; }
    [State(Position = 20)] public partial TimeSpan? RemainingChargingTime { get; internal set; }
    [State(IsDiscrete = true, Position = 21)] public partial bool? IsFastChargerPresent { get; internal set; }
    [State(Position = 22)] public partial string? FastChargerBrand { get; internal set; }
    [State(Position = 23)] public partial string? FastChargerType { get; internal set; }
    [State(Position = 24)] public partial string? ChargeCable { get; internal set; }
    [State(IsDiscrete = true, Position = 25)] public partial bool? IsChargePortDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 26)] public partial TeslaChargePortLatch? ChargePortLatch { get; internal set; }
    [State(Position = 27)] public partial string? ChargePortColor { get; internal set; }
    [State(IsDiscrete = true, Position = 28)] public partial bool? IsChargePortColdWeatherMode { get; internal set; }
    [State(IsDiscrete = true, Position = 29)] public partial bool? IsBatteryHeaterOn { get; internal set; }
    [State(IsDiscrete = true, Position = 30)] public partial TeslaScheduledChargingMode? ScheduledChargingMode { get; internal set; }
    [State(IsDiscrete = true, Position = 31)] public partial bool? IsScheduledChargingPending { get; internal set; }
    [State(Position = 32)] public partial TimeSpan? ScheduledChargingStartTime { get; internal set; }
    [State(Position = 33)] public partial TimeSpan? ScheduledDepartureTime { get; internal set; }
    [State(IsDiscrete = true, Position = 34)] public partial bool? IsOffPeakChargingEnabled { get; internal set; }
    [State(IsDiscrete = true, Position = 35)] public partial bool? IsPreconditioningEnabled { get; internal set; }
    [State(IsDiscrete = true, Position = 36)] public partial bool? IsTripCharging { get; internal set; }
    [State(Position = 37)] public partial int? MaximumRangeChargeCounter { get; internal set; }

    /// <summary>Whether a cable is plugged in, from the charging state.</summary>
    [Derived]
    [State(IsDiscrete = true, Position = 38)]
    public bool? IsPluggedIn => ChargingState switch
    {
        null or TeslaChargingState.Unknown => null,
        TeslaChargingState.Disconnected => false,
        _ => true
    };

    /// <summary>Whether the vehicle charges, from the charging state.</summary>
    [Derived]
    [State(IsDiscrete = true, Position = 39)]
    public bool? IsCharging => ChargingState switch
    {
        null or TeslaChargingState.Unknown => null,
        TeslaChargingState.Charging or TeslaChargingState.Starting => true,
        _ => false
    };

    /// <summary>The usable battery capacity, estimated from the energy and the range added while charging (from HomeBlaze v1).</summary>
    [Derived]
    [State(Unit = StateUnit.WattHour, IsEstimated = true, Position = 40)]
    public decimal? EstimatedBatteryCapacity => GetEstimatedBatteryCapacity();

    public TeslaChargeState(ILogger logger)
    {
        _logger = logger;
        ChargingState = null;
        BatteryLevel = null;
        UsableBatteryLevel = null;
        ChargeLimit = null;
        MinimumChargeLimit = null;
        MaximumChargeLimit = null;
        StandardChargeLimit = null;
        Range = null;
        IdealRange = null;
        EstimatedRange = null;
        ChargingPower = null;
        ChargingCurrent = null;
        RequestedChargingCurrent = null;
        MaximumRequestedChargingCurrent = null;
        PilotCurrent = null;
        ChargerVoltage = null;
        ChargerPhases = null;
        AddedEnergy = null;
        AddedRange = null;
        RemainingChargingTime = null;
        IsFastChargerPresent = null;
        FastChargerBrand = null;
        FastChargerType = null;
        ChargeCable = null;
        IsChargePortDoorOpen = null;
        ChargePortLatch = null;
        ChargePortColor = null;
        IsChargePortColdWeatherMode = null;
        IsBatteryHeaterOn = null;
        ScheduledChargingMode = null;
        IsScheduledChargingPending = null;
        ScheduledChargingStartTime = null;
        ScheduledDepartureTime = null;
        IsOffPeakChargingEnabled = null;
        IsPreconditioningEnabled = null;
        IsTripCharging = null;
        MaximumRangeChargeCounter = null;
    }

    internal void Update(TeslaChargeStateDto dto)
    {
        if (!_clock.TryAdvance(TeslaValues.FromUnixMilliseconds(dto.Timestamp)))
        {
            return;
        }

        ChargingState = TeslaValues.Enum<TeslaChargingState>(dto.ChargingState, _logger);
        BatteryLevel = TeslaValues.Percent(dto.BatteryLevel);
        UsableBatteryLevel = TeslaValues.Percent(dto.UsableBatteryLevel);
        ChargeLimit = TeslaValues.Percent(dto.ChargeLimitSoc);
        MinimumChargeLimit = TeslaValues.Percent(dto.ChargeLimitSocMin);
        MaximumChargeLimit = TeslaValues.Percent(dto.ChargeLimitSocMax);
        StandardChargeLimit = TeslaValues.Percent(dto.ChargeLimitSocStd);
        Range = TeslaValues.MilesToKilometers(dto.BatteryRange);
        IdealRange = TeslaValues.MilesToKilometers(dto.IdealBatteryRange);
        EstimatedRange = TeslaValues.MilesToKilometers(dto.EstBatteryRange);
        ChargingPower = TeslaValues.KilowattsToWatts(dto.ChargerPower);
        ChargingCurrent = dto.ChargerActualCurrent;
        RequestedChargingCurrent = dto.ChargeCurrentRequest;
        MaximumRequestedChargingCurrent = dto.ChargeCurrentRequestMax;
        PilotCurrent = dto.ChargerPilotCurrent;
        ChargerVoltage = dto.ChargerVoltage > MinimumChargerVoltage ? dto.ChargerVoltage : null;
        ChargerPhases = dto.ChargerPhases;
        AddedEnergy = TeslaValues.KilowattHoursToWattHours(dto.ChargeEnergyAdded);
        AddedRange = TeslaValues.MilesToKilometers(dto.ChargeMilesAddedRated);
        RemainingChargingTime = dto.MinutesToFullCharge is > 0 ? TimeSpan.FromMinutes(dto.MinutesToFullCharge.Value) : null;
        IsFastChargerPresent = dto.FastChargerPresent;
        FastChargerBrand = TeslaValues.Text(dto.FastChargerBrand);
        FastChargerType = TeslaValues.Text(dto.FastChargerType);
        ChargeCable = TeslaValues.Text(dto.ConnChargeCable);
        IsChargePortDoorOpen = dto.ChargePortDoorOpen;
        ChargePortLatch = TeslaValues.Enum<TeslaChargePortLatch>(dto.ChargePortLatch, _logger);
        ChargePortColor = TeslaValues.Text(dto.ChargePortColor);
        IsChargePortColdWeatherMode = dto.ChargePortColdWeatherMode;
        IsBatteryHeaterOn = dto.BatteryHeaterOn;
        ScheduledChargingMode = TeslaValues.Enum<TeslaScheduledChargingMode>(dto.ScheduledChargingMode, _logger);
        IsScheduledChargingPending = dto.ScheduledChargingPending;
        ScheduledChargingStartTime = TeslaValues.MinutesAfterMidnight(dto.ScheduledChargingStartTimeMinutes);
        ScheduledDepartureTime = dto.ScheduledDepartureTime is null ? null : TeslaValues.MinutesAfterMidnight(dto.ScheduledDepartureTimeMinutes);
        IsOffPeakChargingEnabled = dto.OffPeakChargingEnabled;
        IsPreconditioningEnabled = dto.PreconditioningEnabled;
        IsTripCharging = dto.TripCharging;
        MaximumRangeChargeCounter = dto.MaxRangeChargeCounter;
    }

    internal void Update(ChargeState proto)
    {
        if (!_clock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateChargingState(proto);
        UpdateBattery(proto);
        UpdateRange(proto);
        UpdatePower(proto);
        UpdatePortAndSchedule(proto);
    }

    private void UpdateChargingState(ChargeState proto)
    {
        if (proto.ChargingState is { } chargingState)
        {
            ChargingState = chargingState.TypeCase switch
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
        }
    }

    private void UpdateBattery(ChargeState proto)
    {
        if (proto.OptionalBatteryLevelCase != ChargeState.OptionalBatteryLevelOneofCase.None) BatteryLevel = TeslaValues.Percent(proto.BatteryLevel);
        if (proto.OptionalUsableBatteryLevelCase != ChargeState.OptionalUsableBatteryLevelOneofCase.None) UsableBatteryLevel = TeslaValues.Percent(proto.UsableBatteryLevel);
        if (proto.OptionalChargeLimitSocCase != ChargeState.OptionalChargeLimitSocOneofCase.None) ChargeLimit = TeslaValues.Percent(proto.ChargeLimitSoc);
        if (proto.OptionalChargeLimitSocMinCase != ChargeState.OptionalChargeLimitSocMinOneofCase.None) MinimumChargeLimit = TeslaValues.Percent(proto.ChargeLimitSocMin);
        if (proto.OptionalChargeLimitSocMaxCase != ChargeState.OptionalChargeLimitSocMaxOneofCase.None) MaximumChargeLimit = TeslaValues.Percent(proto.ChargeLimitSocMax);
        if (proto.OptionalChargeLimitSocStdCase != ChargeState.OptionalChargeLimitSocStdOneofCase.None) StandardChargeLimit = TeslaValues.Percent(proto.ChargeLimitSocStd);
    }

    private void UpdateRange(ChargeState proto)
    {
        if (proto.OptionalBatteryRangeCase != ChargeState.OptionalBatteryRangeOneofCase.None) Range = TeslaValues.MilesToKilometers((decimal)proto.BatteryRange);
        if (proto.OptionalIdealBatteryRangeCase != ChargeState.OptionalIdealBatteryRangeOneofCase.None) IdealRange = TeslaValues.MilesToKilometers((decimal)proto.IdealBatteryRange);
        if (proto.OptionalEstBatteryRangeCase != ChargeState.OptionalEstBatteryRangeOneofCase.None) EstimatedRange = TeslaValues.MilesToKilometers((decimal)proto.EstBatteryRange);
        if (proto.OptionalChargeEnergyAddedCase != ChargeState.OptionalChargeEnergyAddedOneofCase.None) AddedEnergy = TeslaValues.KilowattHoursToWattHours((decimal)proto.ChargeEnergyAdded);
        if (proto.OptionalChargeMilesAddedRatedCase != ChargeState.OptionalChargeMilesAddedRatedOneofCase.None) AddedRange = TeslaValues.MilesToKilometers((decimal)proto.ChargeMilesAddedRated);
        if (proto.OptionalMinutesToChargeLimitCase != ChargeState.OptionalMinutesToChargeLimitOneofCase.None) RemainingChargingTime = proto.MinutesToChargeLimit > 0 ? TimeSpan.FromMinutes(proto.MinutesToChargeLimit) : null;
    }

    private void UpdatePower(ChargeState proto)
    {
        if (proto.OptionalChargerPowerCase != ChargeState.OptionalChargerPowerOneofCase.None) ChargingPower = TeslaValues.KilowattsToWatts(proto.ChargerPower);
        if (proto.OptionalChargerActualCurrentCase != ChargeState.OptionalChargerActualCurrentOneofCase.None) ChargingCurrent = proto.ChargerActualCurrent;
        if (proto.OptionalChargeCurrentRequestCase != ChargeState.OptionalChargeCurrentRequestOneofCase.None) RequestedChargingCurrent = proto.ChargeCurrentRequest;
        if (proto.OptionalChargeCurrentRequestMaxCase != ChargeState.OptionalChargeCurrentRequestMaxOneofCase.None) MaximumRequestedChargingCurrent = proto.ChargeCurrentRequestMax;
        if (proto.OptionalChargerPilotCurrentCase != ChargeState.OptionalChargerPilotCurrentOneofCase.None) PilotCurrent = proto.ChargerPilotCurrent;
        if (proto.OptionalChargerVoltageCase != ChargeState.OptionalChargerVoltageOneofCase.None) ChargerVoltage = proto.ChargerVoltage > MinimumChargerVoltage ? proto.ChargerVoltage : null;
        if (proto.OptionalChargerPhasesCase != ChargeState.OptionalChargerPhasesOneofCase.None) ChargerPhases = proto.ChargerPhases;
    }

    private void UpdatePortAndSchedule(ChargeState proto)
    {
        if (proto.OptionalFastChargerPresentCase != ChargeState.OptionalFastChargerPresentOneofCase.None) IsFastChargerPresent = proto.FastChargerPresent;
        if (proto.OptionalChargePortDoorOpenCase != ChargeState.OptionalChargePortDoorOpenOneofCase.None) IsChargePortDoorOpen = proto.ChargePortDoorOpen;
        if (proto.OptionalChargePortColdWeatherModeCase != ChargeState.OptionalChargePortColdWeatherModeOneofCase.None) IsChargePortColdWeatherMode = proto.ChargePortColdWeatherMode;
        if (proto.OptionalScheduledChargingPendingCase != ChargeState.OptionalScheduledChargingPendingOneofCase.None) IsScheduledChargingPending = proto.ScheduledChargingPending;
        if (proto.OptionalScheduledChargingStartTimeMinutesCase != ChargeState.OptionalScheduledChargingStartTimeMinutesOneofCase.None) ScheduledChargingStartTime = TeslaValues.MinutesAfterMidnight((int)proto.ScheduledChargingStartTimeMinutes);
        if (proto.OptionalPreconditioningEnabledCase != ChargeState.OptionalPreconditioningEnabledOneofCase.None) IsPreconditioningEnabled = proto.PreconditioningEnabled;
        if (proto.OptionalTripChargingCase != ChargeState.OptionalTripChargingOneofCase.None) IsTripCharging = proto.TripCharging;
        if (proto.OptionalMaxRangeChargeCounterCase != ChargeState.OptionalMaxRangeChargeCounterOneofCase.None) MaximumRangeChargeCounter = proto.MaxRangeChargeCounter;
    }

    private decimal? GetEstimatedBatteryCapacity()
    {
        // Energy added over range added, scaled to the full ideal range. Only meaningful after more than 5 kWh were added.
        if (AddedEnergy is not > 5000m || IdealRange is not > 0 || UsableBatteryLevel is not > 0 || AddedRange is not > 0)
        {
            return null;
        }

        var fullIdealRange = IdealRange.Value / UsableBatteryLevel.Value;
        var capacity = fullIdealRange / AddedRange.Value * AddedEnergy.Value;
        return capacity > 10_000m ? Math.Round(capacity, 0) : null;
    }
}
