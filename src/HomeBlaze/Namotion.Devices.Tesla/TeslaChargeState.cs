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
/// The charging state of a Tesla vehicle.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaChargeState : IVehicleChargingState, ITitleProvider, IIconProvider
{
    private const decimal MinimumChargerVoltage = 2m;

    private readonly ILogger _logger;
    private readonly TeslaSectionClock _clock = new();

    public string Title => "Charge";

    public string IconName => "EvStation";

    [State(IsDiscrete = true, Position = 1)] public partial TeslaChargingState? ChargingState { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 2)] public partial decimal? BatteryLevel { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 3)] public partial decimal? UsableBatteryLevel { get; internal set; }
    [Category("")]
    [State(Unit = StateUnit.Percent, Position = 4)] public partial decimal? ChargeLimit { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 5)] public partial decimal? MinimumChargeLimit { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 6)] public partial decimal? MaximumChargeLimit { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 7)] public partial decimal? StandardChargeLimit { get; internal set; }

    /// <summary>The rated range, which Tesla reports as battery range.</summary>
    [Category("")]
    [State(Unit = StateUnit.Kilometer, Position = 8)] public partial decimal? Range { get; internal set; }

    [State(Unit = StateUnit.Kilometer, Position = 9)] public partial decimal? IdealRange { get; internal set; }
    [State(Unit = StateUnit.Kilometer, IsEstimated = true, Position = 10)] public partial decimal? EstimatedRange { get; internal set; }

    /// <summary>The charging power, which Tesla reports with a resolution of 1 kW.</summary>
    [Category("")]
    [State(Unit = StateUnit.Watt, Position = 11)] public partial decimal? ChargingPower { get; internal set; }

    [State(Unit = StateUnit.Ampere, Position = 12)] public partial decimal? ChargingCurrent { get; internal set; }

    /// <summary>The current that the vehicle requests from the charger.</summary>
    [State(Unit = StateUnit.Ampere, Position = 13)] public partial decimal? RequestedChargingCurrent { get; internal set; }

    /// <summary>The highest current that the vehicle can request.</summary>
    [State(Unit = StateUnit.Ampere, Position = 14)] public partial decimal? MaximumRequestedChargingCurrent { get; internal set; }

    /// <summary>The current that the charger offers through the pilot signal.</summary>
    [State(Unit = StateUnit.Ampere, Position = 15)] public partial decimal? PilotCurrent { get; internal set; }

    /// <summary>The charger voltage, or null when not charging (Tesla reports about 1 V then).</summary>
    [State(Unit = StateUnit.Volt, Position = 16)] public partial decimal? ChargerVoltage { get; internal set; }

    [State(Position = 17)] public partial int? ChargerPhases { get; internal set; }
    [State(Unit = StateUnit.WattHour, Position = 18)] public partial decimal? AddedEnergy { get; internal set; }

    /// <summary>The rated range added by the current or last charging session.</summary>
    [State(Unit = StateUnit.Kilometer, Position = 19)] public partial decimal? AddedRange { get; internal set; }

    /// <summary>The time until the charge limit is reached, or null when the vehicle does not charge.</summary>
    [Category("")]
    [State(Position = 20)] public partial TimeSpan? RemainingChargingTime { get; internal set; }

    [State(IsDiscrete = true, Position = 21)] public partial bool? IsFastChargerPresent { get; internal set; }
    [State(Position = 22)] public partial string? FastChargerBrand { get; internal set; }
    [State(Position = 23)] public partial string? FastChargerType { get; internal set; }
    [State(Position = 24)] public partial string? ChargeCable { get; internal set; }
    [State(IsDiscrete = true, Position = 25)] public partial bool? IsChargePortDoorOpen { get; internal set; }
    [State(IsDiscrete = true, Position = 26)] public partial TeslaChargePortLatch? ChargePortLatch { get; internal set; }
    [State(Position = 27)] public partial string? ChargePortColor { get; internal set; }
    [State(IsDiscrete = true, Position = 28)] public partial bool? IsChargePortColdWeatherMode { get; internal set; }
    [State(IsDiscrete = true, Position = 30)] public partial TeslaScheduledChargingMode? ScheduledChargingMode { get; internal set; }
    [State(IsDiscrete = true, Position = 31)] public partial bool? IsScheduledChargingPending { get; internal set; }

    /// <summary>The time of day at which scheduled charging starts.</summary>
    [State(Position = 32)] public partial TimeSpan? ScheduledChargingStartTime { get; internal set; }

    /// <summary>The time of day by which the vehicle is charged, or null when no departure is scheduled.</summary>
    [State(Position = 33)] public partial TimeSpan? ScheduledDepartureTime { get; internal set; }

    [State(IsDiscrete = true, Position = 34)] public partial bool? IsOffPeakChargingEnabled { get; internal set; }
    [State(IsDiscrete = true, Position = 35)] public partial bool? IsPreconditioningEnabled { get; internal set; }
    [State(IsDiscrete = true, Position = 36)] public partial bool? IsTripCharging { get; internal set; }

    /// <summary>How often the vehicle charged to the maximum range limit.</summary>
    [State(Position = 37)] public partial int? MaximumRangeChargeCounter { get; internal set; }

    /// <summary>The charging current that is set in the vehicle or app.</summary>
    [State(Unit = StateUnit.Ampere, Position = 38)] public partial decimal? ChargingCurrentSetting { get; internal set; }

    /// <summary>The speed at which the rated range grows while charging.</summary>
    [State(Unit = StateUnit.KilometerPerHour, Position = 39)] public partial decimal? ChargeRate { get; internal set; }

    /// <summary>The ideal range added by the current or last charging session.</summary>
    [State(Unit = StateUnit.Kilometer, Position = 40)] public partial decimal? AddedIdealRange { get; internal set; }

    /// <summary>Whether charging is enabled in the vehicle.</summary>
    [State(IsDiscrete = true, Position = 41)] public partial bool? IsChargeEnabled { get; internal set; }

    /// <summary>Whether the user enabled charging.</summary>
    [State(IsDiscrete = true, Position = 42)] public partial bool? IsChargeEnabledByUser { get; internal set; }

    /// <summary>Whether the Supercharger session was started from the trip planner.</summary>
    [State(IsDiscrete = true, Position = 44)] public partial bool? IsSuperchargerSessionFromTripPlanner { get; internal set; }

    /// <summary>The days on which off-peak charging applies.</summary>
    [State(IsDiscrete = true, Position = 45)] public partial TeslaScheduleDays? OffPeakChargingDays { get; internal set; }

    /// <summary>The time of day at which the off-peak hours end.</summary>
    [State(Position = 46)] public partial TimeSpan? OffPeakChargingEndTime { get; internal set; }

    /// <summary>The days on which preconditioning applies.</summary>
    [State(IsDiscrete = true, Position = 47)] public partial TeslaScheduleDays? PreconditioningDays { get; internal set; }

    /// <summary>Whether a cable is plugged in, from the charging state.</summary>
    [Derived]
    [Category("")]
    [State(IsDiscrete = true, Position = 48)]
    public bool? IsPluggedIn => ChargingState switch
    {
        null or TeslaChargingState.Unknown => null,
        TeslaChargingState.Disconnected => false,
        _ => true
    };

    /// <summary>Whether the vehicle charges, from the charging state.</summary>
    [Derived]
    [Category("")]
    [State(IsDiscrete = true, Position = 49)]
    public bool? IsCharging => ChargingState switch
    {
        null or TeslaChargingState.Unknown => null,
        TeslaChargingState.Charging or TeslaChargingState.Starting => true,
        _ => false
    };

    /// <summary>The usable battery capacity, estimated from the energy and the ideal range added while charging.</summary>
    [Derived]
    [State(Unit = StateUnit.WattHour, IsEstimated = true, Position = 50)]
    public decimal? EstimatedBatteryCapacity => GetEstimatedBatteryCapacity();

    /// <summary>
    /// The charger voltage times the highest current that the charger offers and the vehicle can request; not null whenever a
    /// charger voltage is reported, for example while plugged in.
    /// </summary>
    [Derived]
    [Category("")]
    [State(Unit = StateUnit.Watt, Position = 51)]
    public decimal? MaximumChargingPower => ChargerVoltage * GetMaximumChargingCurrent() * GetChargingPhases();

    internal TeslaChargeState(ILogger logger)
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
        ScheduledChargingMode = null;
        IsScheduledChargingPending = null;
        ScheduledChargingStartTime = null;
        ScheduledDepartureTime = null;
        IsOffPeakChargingEnabled = null;
        IsPreconditioningEnabled = null;
        IsTripCharging = null;
        MaximumRangeChargeCounter = null;
        ChargingCurrentSetting = null;
        ChargeRate = null;
        AddedIdealRange = null;
        IsChargeEnabled = null;
        IsChargeEnabledByUser = null;
        IsSuperchargerSessionFromTripPlanner = null;
        OffPeakChargingDays = null;
        OffPeakChargingEndTime = null;
        PreconditioningDays = null;
    }

    internal void Update(TeslaChargeStateDto dto)
    {
        if (!_clock.TryAdvance(TeslaValues.FromUnixMilliseconds(dto.Timestamp)))
        {
            return;
        }

        UpdateBatteryFromJson(dto);
        UpdatePowerFromJson(dto);
        UpdateChargerFromJson(dto);
        UpdateScheduleFromJson(dto);
    }

    internal void Update(ChargeState proto)
    {
        if (!_clock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateBatteryFromProtobuf(proto);
        UpdateRangeFromProtobuf(proto);
        UpdatePowerFromProtobuf(proto);
        UpdateChargerFromProtobuf(proto);
        UpdateScheduleFromProtobuf(proto);
        UpdateScheduleDaysFromProtobuf(proto);
    }

    private void UpdateBatteryFromJson(TeslaChargeStateDto dto)
    {
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
        AddedEnergy = TeslaValues.KilowattHoursToWattHours(dto.ChargeEnergyAdded);
        AddedRange = TeslaValues.MilesToKilometers(dto.ChargeMilesAddedRated);
        AddedIdealRange = TeslaValues.MilesToKilometers(dto.ChargeMilesAddedIdeal);
    }

    private void UpdatePowerFromJson(TeslaChargeStateDto dto)
    {
        ChargingPower = TeslaValues.KilowattsToWatts(dto.ChargerPower);
        ChargingCurrent = dto.ChargerActualCurrent;
        RequestedChargingCurrent = dto.ChargeCurrentRequest;
        MaximumRequestedChargingCurrent = dto.ChargeCurrentRequestMax;
        PilotCurrent = dto.ChargerPilotCurrent;
        ChargingCurrentSetting = dto.ChargeAmps;
        ChargerVoltage = dto.ChargerVoltage > MinimumChargerVoltage ? dto.ChargerVoltage : null;
        ChargerPhases = dto.ChargerPhases;
        ChargeRate = TeslaValues.MilesPerHourToKilometersPerHour(dto.ChargeRate);
        RemainingChargingTime = dto.MinutesToFullCharge is > 0 ? TimeSpan.FromMinutes(dto.MinutesToFullCharge.Value) : null;
    }

    private void UpdateChargerFromJson(TeslaChargeStateDto dto)
    {
        IsFastChargerPresent = dto.FastChargerPresent;
        FastChargerBrand = TeslaValues.Text(dto.FastChargerBrand);
        FastChargerType = TeslaValues.Text(dto.FastChargerType);
        ChargeCable = TeslaValues.Text(dto.ConnChargeCable);
        IsChargePortDoorOpen = dto.ChargePortDoorOpen;
        ChargePortLatch = TeslaValues.Enum<TeslaChargePortLatch>(dto.ChargePortLatch, _logger);
        ChargePortColor = TeslaValues.Text(dto.ChargePortColor);
        IsChargePortColdWeatherMode = dto.ChargePortColdWeatherMode;
        IsChargeEnabled = dto.ChargeEnableRequest;
        IsChargeEnabledByUser = dto.UserChargeEnableRequest;
        IsSuperchargerSessionFromTripPlanner = dto.SuperchargerSessionTripPlanner;
        IsTripCharging = dto.TripCharging;
        MaximumRangeChargeCounter = dto.MaxRangeChargeCounter;
    }

    private void UpdateScheduleFromJson(TeslaChargeStateDto dto)
    {
        ScheduledChargingMode = TeslaValues.Enum<TeslaScheduledChargingMode>(dto.ScheduledChargingMode, _logger);
        IsScheduledChargingPending = dto.ScheduledChargingPending;
        ScheduledChargingStartTime = TeslaValues.MinutesAfterMidnight(dto.ScheduledChargingStartTimeMinutes);
        ScheduledDepartureTime = dto.ScheduledDepartureTime is null ? null : TeslaValues.MinutesAfterMidnight(dto.ScheduledDepartureTimeMinutes);
        IsOffPeakChargingEnabled = dto.OffPeakChargingEnabled;
        OffPeakChargingDays = TeslaValues.Enum<TeslaScheduleDays>(dto.OffPeakChargingTimes, _logger);
        OffPeakChargingEndTime = TeslaValues.MinutesAfterMidnight(dto.OffPeakHoursEndTime);
        IsPreconditioningEnabled = dto.PreconditioningEnabled;
        PreconditioningDays = TeslaValues.Enum<TeslaScheduleDays>(dto.PreconditioningTimes, _logger);
    }

    private void UpdateBatteryFromProtobuf(ChargeState proto)
    {
        if (proto.ChargingState is { } chargingState) ChargingState = TeslaProtoValues.ChargingState(chargingState);
        if (proto.OptionalBatteryLevelCase != ChargeState.OptionalBatteryLevelOneofCase.None) BatteryLevel = TeslaValues.Percent(proto.BatteryLevel);
        if (proto.OptionalUsableBatteryLevelCase != ChargeState.OptionalUsableBatteryLevelOneofCase.None) UsableBatteryLevel = TeslaValues.Percent(proto.UsableBatteryLevel);
        if (proto.OptionalChargeLimitSocCase != ChargeState.OptionalChargeLimitSocOneofCase.None) ChargeLimit = TeslaValues.Percent(proto.ChargeLimitSoc);
        if (proto.OptionalChargeLimitSocMinCase != ChargeState.OptionalChargeLimitSocMinOneofCase.None) MinimumChargeLimit = TeslaValues.Percent(proto.ChargeLimitSocMin);
        if (proto.OptionalChargeLimitSocMaxCase != ChargeState.OptionalChargeLimitSocMaxOneofCase.None) MaximumChargeLimit = TeslaValues.Percent(proto.ChargeLimitSocMax);
        if (proto.OptionalChargeLimitSocStdCase != ChargeState.OptionalChargeLimitSocStdOneofCase.None) StandardChargeLimit = TeslaValues.Percent(proto.ChargeLimitSocStd);
    }

    private void UpdateRangeFromProtobuf(ChargeState proto)
    {
        if (proto.OptionalBatteryRangeCase != ChargeState.OptionalBatteryRangeOneofCase.None) Range = TeslaValues.MilesToKilometers(TeslaValues.Miles(proto.BatteryRange));
        if (proto.OptionalIdealBatteryRangeCase != ChargeState.OptionalIdealBatteryRangeOneofCase.None) IdealRange = TeslaValues.MilesToKilometers(TeslaValues.Miles(proto.IdealBatteryRange));
        if (proto.OptionalEstBatteryRangeCase != ChargeState.OptionalEstBatteryRangeOneofCase.None) EstimatedRange = TeslaValues.MilesToKilometers(TeslaValues.Miles(proto.EstBatteryRange));
        if (proto.OptionalChargeEnergyAddedCase != ChargeState.OptionalChargeEnergyAddedOneofCase.None) AddedEnergy = TeslaValues.KilowattHoursToWattHours(TeslaValues.Decimal(proto.ChargeEnergyAdded));
        if (proto.OptionalChargeMilesAddedRatedCase != ChargeState.OptionalChargeMilesAddedRatedOneofCase.None) AddedRange = TeslaValues.MilesToKilometers(TeslaValues.Miles(proto.ChargeMilesAddedRated));
        if (proto.OptionalChargeMilesAddedIdealCase != ChargeState.OptionalChargeMilesAddedIdealOneofCase.None) AddedIdealRange = TeslaValues.MilesToKilometers(TeslaValues.Miles(proto.ChargeMilesAddedIdeal));
    }

    private void UpdatePowerFromProtobuf(ChargeState proto)
    {
        if (proto.OptionalChargerPowerCase != ChargeState.OptionalChargerPowerOneofCase.None) ChargingPower = TeslaValues.KilowattsToWatts(proto.ChargerPower);
        if (proto.OptionalChargerActualCurrentCase != ChargeState.OptionalChargerActualCurrentOneofCase.None) ChargingCurrent = proto.ChargerActualCurrent;
        if (proto.OptionalChargeCurrentRequestCase != ChargeState.OptionalChargeCurrentRequestOneofCase.None) RequestedChargingCurrent = proto.ChargeCurrentRequest;
        if (proto.OptionalChargeCurrentRequestMaxCase != ChargeState.OptionalChargeCurrentRequestMaxOneofCase.None) MaximumRequestedChargingCurrent = proto.ChargeCurrentRequestMax;
        if (proto.OptionalChargerPilotCurrentCase != ChargeState.OptionalChargerPilotCurrentOneofCase.None) PilotCurrent = proto.ChargerPilotCurrent;
        if (proto.OptionalChargingAmpsCase != ChargeState.OptionalChargingAmpsOneofCase.None) ChargingCurrentSetting = proto.ChargingAmps;
        if (proto.OptionalChargerVoltageCase != ChargeState.OptionalChargerVoltageOneofCase.None) ChargerVoltage = proto.ChargerVoltage > MinimumChargerVoltage ? proto.ChargerVoltage : null;
        if (proto.OptionalChargerPhasesCase != ChargeState.OptionalChargerPhasesOneofCase.None) ChargerPhases = proto.ChargerPhases;
        UpdateChargeRateFromProtobuf(proto);
        UpdateRemainingChargingTimeFromProtobuf(proto);
    }

    private void UpdateChargeRateFromProtobuf(ChargeState proto)
    {
        if (proto.OptionalChargeRateMphFloatCase != ChargeState.OptionalChargeRateMphFloatOneofCase.None) ChargeRate = TeslaValues.MilesPerHourToKilometersPerHour(TeslaValues.Decimal(proto.ChargeRateMphFloat));
        else if (proto.OptionalChargeRateMphCase != ChargeState.OptionalChargeRateMphOneofCase.None) ChargeRate = TeslaValues.MilesPerHourToKilometersPerHour(proto.ChargeRateMph);
    }

    // The JSON minutes_to_full_charge is the time to the charge limit, as is minutes_to_charge_limit.
    private void UpdateRemainingChargingTimeFromProtobuf(ChargeState proto)
    {
        int? minutes = null;
        if (proto.OptionalMinutesToChargeLimitCase != ChargeState.OptionalMinutesToChargeLimitOneofCase.None) minutes = proto.MinutesToChargeLimit;
        else if (proto.OptionalMinutesToFullChargeCase != ChargeState.OptionalMinutesToFullChargeOneofCase.None) minutes = proto.MinutesToFullCharge;

        if (minutes is not null)
        {
            RemainingChargingTime = minutes > 0 ? TimeSpan.FromMinutes(minutes.Value) : null;
        }
    }

    private void UpdateChargerFromProtobuf(ChargeState proto)
    {
        if (proto.OptionalFastChargerPresentCase != ChargeState.OptionalFastChargerPresentOneofCase.None) IsFastChargerPresent = proto.FastChargerPresent;
        if (proto.FastChargerBrand is { } brand && TeslaProtoValues.FastChargerBrand(brand) is { } mappedBrand) FastChargerBrand = mappedBrand;
        if (proto.FastChargerType is { } type && TeslaProtoValues.FastChargerType(type) is { } mappedType) FastChargerType = mappedType;
        if (proto.ConnChargeCable is { } cable && TeslaProtoValues.ChargeCable(cable) is { } mappedCable) ChargeCable = mappedCable;
        if (proto.OptionalChargePortDoorOpenCase != ChargeState.OptionalChargePortDoorOpenOneofCase.None) IsChargePortDoorOpen = proto.ChargePortDoorOpen;
        UpdateChargePortFromProtobuf(proto);
        UpdateFlagsFromProtobuf(proto);
    }

    private void UpdateChargePortFromProtobuf(ChargeState proto)
    {
        if (proto.ChargePortLatch is { } latch && TeslaProtoValues.ChargePortLatch(latch) is { } mappedLatch) ChargePortLatch = mappedLatch;
        if (proto.OptionalChargePortColorCase != ChargeState.OptionalChargePortColorOneofCase.None && TeslaProtoValues.ChargePortColor(TeslaProtoValues.Checked(proto.ChargePortColor, _logger)) is { } mappedColor) ChargePortColor = mappedColor;
        if (proto.OptionalChargePortColdWeatherModeCase != ChargeState.OptionalChargePortColdWeatherModeOneofCase.None) IsChargePortColdWeatherMode = proto.ChargePortColdWeatherMode;
    }

    private void UpdateFlagsFromProtobuf(ChargeState proto)
    {
        if (proto.OptionalChargeEnableRequestCase != ChargeState.OptionalChargeEnableRequestOneofCase.None) IsChargeEnabled = proto.ChargeEnableRequest;
        if (proto.OptionalUserChargeEnableRequestCase != ChargeState.OptionalUserChargeEnableRequestOneofCase.None) IsChargeEnabledByUser = proto.UserChargeEnableRequest;
        if (proto.OptionalSuperchargerSessionTripPlannerCase != ChargeState.OptionalSuperchargerSessionTripPlannerOneofCase.None) IsSuperchargerSessionFromTripPlanner = proto.SuperchargerSessionTripPlanner;
        if (proto.OptionalTripChargingCase != ChargeState.OptionalTripChargingOneofCase.None) IsTripCharging = proto.TripCharging;
        if (proto.OptionalMaxRangeChargeCounterCase != ChargeState.OptionalMaxRangeChargeCounterOneofCase.None) MaximumRangeChargeCounter = proto.MaxRangeChargeCounter;
    }

    private void UpdateScheduleFromProtobuf(ChargeState proto)
    {
        if (proto.OptionalScheduledChargingModeCase != ChargeState.OptionalScheduledChargingModeOneofCase.None && TeslaProtoValues.ScheduledChargingMode(TeslaProtoValues.Checked(proto.ScheduledChargingMode, _logger)) is { } mappedMode) ScheduledChargingMode = mappedMode;
        if (proto.OptionalScheduledChargingPendingCase != ChargeState.OptionalScheduledChargingPendingOneofCase.None) IsScheduledChargingPending = proto.ScheduledChargingPending;
        if (proto.OptionalScheduledChargingStartTimeMinutesCase != ChargeState.OptionalScheduledChargingStartTimeMinutesOneofCase.None) ScheduledChargingStartTime = TeslaValues.MinutesAfterMidnight((int)proto.ScheduledChargingStartTimeMinutes);
        if (proto.OptionalPreconditioningEnabledCase != ChargeState.OptionalPreconditioningEnabledOneofCase.None) IsPreconditioningEnabled = proto.PreconditioningEnabled;
        if (proto.OptionalScheduledDepartureTimeMinutesCase != ChargeState.OptionalScheduledDepartureTimeMinutesOneofCase.None) ScheduledDepartureTime = proto.ScheduledDepartureTime is null ? null : TeslaValues.MinutesAfterMidnight((int)proto.ScheduledDepartureTimeMinutes);
    }

    // The off-peak days of the snapshot disagree with the JSON (all week versus weekdays while off-peak charging is disabled), so only the JSON sets OffPeakChargingDays.
    private void UpdateScheduleDaysFromProtobuf(ChargeState proto)
    {
        if (proto.OptionalOffPeakHoursEndTimeCase != ChargeState.OptionalOffPeakHoursEndTimeOneofCase.None) OffPeakChargingEndTime = TeslaValues.MinutesAfterMidnight((int)proto.OffPeakHoursEndTime);
        if (proto.PreconditioningTimes is { } preconditioningTimes && TeslaProtoValues.Days(preconditioningTimes) is { } preconditioningDays) PreconditioningDays = preconditioningDays;
    }

    private decimal? GetEstimatedBatteryCapacity()
    {
        // Energy added over ideal range added, scaled to the full ideal range. Only meaningful after more than 5 kWh were added.
        if (AddedEnergy is not > 5000m || IdealRange is not > 0 || UsableBatteryLevel is not > 0 || AddedIdealRange is not > 0)
        {
            return null;
        }

        var fullIdealRange = IdealRange.Value / UsableBatteryLevel.Value;
        var capacity = fullIdealRange / AddedIdealRange.Value * AddedEnergy.Value;
        return capacity > 10_000m ? Math.Round(capacity, 0) : null;
    }

    private decimal? GetMaximumChargingCurrent()
    {
        var pilotCurrent = PilotCurrent;
        var maximumRequestedCurrent = MaximumRequestedChargingCurrent;
        return pilotCurrent is { } pilot && maximumRequestedCurrent is { } maximum ? Math.Min(pilot, maximum) : pilotCurrent ?? maximumRequestedCurrent;
    }

    // Tesla is reported to send 2 phases while charging on 3-phase AC, so the measured power decides when it is known.
    private decimal GetChargingPhases()
    {
        var power = ChargingPower;
        var voltage = ChargerVoltage;
        var current = ChargingCurrent;
        if (power is > 0 && voltage is > 0 && current is > 0)
        {
            return Math.Clamp(Math.Round(power.Value / (voltage.Value * current.Value), MidpointRounding.AwayFromZero), 1m, 3m);
        }

        return ChargerPhases ?? 1;
    }
}
