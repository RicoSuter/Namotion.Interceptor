using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Vehicles;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Hyundai;

/// <summary>The battery, range and charging of a Bluelink vehicle.</summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class BluelinkChargeState : IVehicleChargingState, IVehicleChargingController, ITitleProvider, IIconProvider
{
    private readonly BluelinkVehicle _vehicle;

    internal BluelinkChargeState(BluelinkVehicle vehicle)
    {
        _vehicle = vehicle;
        BatteryLevel = null;
        IsPluggedIn = null;
        IsCharging = null;
        ChargingPower = null;
        ChargeLimit = null;
        FastChargeLimit = null;
        RemainingChargingTime = null;
        Range = null;
    }

    public string Title => "Charge";

    public string IconName => "EvStation";

    [State(Unit = StateUnit.Percent, Position = 1)] public partial decimal? BatteryLevel { get; internal set; }
    [Category("")]
    [State(IsDiscrete = true, Position = 2)] public partial bool? IsPluggedIn { get; internal set; }
    [Category("")]
    [State(IsDiscrete = true, Position = 3)] public partial bool? IsCharging { get; internal set; }
    [Category("")]
    [State(Unit = StateUnit.Watt, Position = 4)] public partial decimal? ChargingPower { get; internal set; }

    /// <summary>The charge limit for AC charging.</summary>
    [Category("")]
    [State(Unit = StateUnit.Percent, Position = 5)] public partial decimal? ChargeLimit { get; internal set; }

    /// <summary>The charge limit for DC fast charging.</summary>
    [State(Unit = StateUnit.Percent, Position = 6)] public partial decimal? FastChargeLimit { get; internal set; }

    [Category("")]
    [State(Position = 7)] public partial TimeSpan? RemainingChargingTime { get; internal set; }
    [Category("")]
    [State(Unit = StateUnit.Kilometer, Position = 8)] public partial decimal? Range { get; internal set; }

    /// <summary>Not reported by the Bluelink API; always null.</summary>
    [Derived]
    [Category("")]
    [State(Unit = StateUnit.Watt, Position = 9)]
    public decimal? MaximumChargingPower => null;

    [Derived]
    [PropertyAttribute("StartCharging", KnownAttributes.IsEnabled)]
    public bool StartCharging_IsEnabled => !_vehicle.IsBusy && _vehicle.Commands.CanSendCommands(_vehicle.Protocol);

    [Derived]
    [PropertyAttribute("StopCharging", KnownAttributes.IsEnabled)]
    public bool StopCharging_IsEnabled => !_vehicle.IsBusy && _vehicle.Commands.CanSendCommands(_vehicle.Protocol);

    // Charge limits need no PIN, also on a CCS2 vehicle.
    [Derived]
    [PropertyAttribute("SetChargeLimit", KnownAttributes.IsEnabled)]
    public bool SetChargeLimit_IsEnabled => !_vehicle.IsBusy && _vehicle.Commands.CanSendCommands(BluelinkProtocol.Legacy);

    [Derived]
    [PropertyAttribute("SetChargeLimits", KnownAttributes.IsEnabled)]
    public bool SetChargeLimits_IsEnabled => !_vehicle.IsBusy && _vehicle.Commands.CanSendCommands(BluelinkProtocol.Legacy);

    /// <inheritdoc />
    [Category("")]
    public Task StartChargingAsync(CancellationToken cancellationToken) =>
        _vehicle.Commands.SetChargingAsync(_vehicle, start: true, cancellationToken);

    /// <inheritdoc />
    [Category("")]
    public Task StopChargingAsync(CancellationToken cancellationToken) =>
        _vehicle.Commands.SetChargingAsync(_vehicle, start: false, cancellationToken);

    /// <summary>Sets the AC charge limit and keeps the current DC charge limit, because the API always takes both.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The limit is not 0.5 to 1.0 in steps of 0.1.</exception>
    /// <exception cref="InvalidOperationException">The DC charge limit is not known yet or is not a setting.</exception>
    [Category("")]
    public Task SetChargeLimitAsync(decimal chargeLimit, CancellationToken cancellationToken)
    {
        var acPercent = BluelinkValues.ToChargeLimitPercent(chargeLimit, nameof(chargeLimit));
        if (FastChargeLimit is not { } fastChargeLimit)
        {
            throw new InvalidOperationException("The DC charge limit is not known yet; use Set Charge Limits to set both.");
        }

        int dcPercent;
        try
        {
            dcPercent = BluelinkValues.ToChargeLimitPercent(fastChargeLimit, nameof(fastChargeLimit));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException("The reported DC charge limit is not a setting; use Set Charge Limits to set both.", exception);
        }

        return _vehicle.Commands.SetChargeLimitsAsync(_vehicle, acPercent, dcPercent, cancellationToken);
    }

    /// <summary>Sets the AC and the DC charge limit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not 0.5 to 1.0 in steps of 0.1.</exception>
    [Operation(Title = "Set Charge Limits", Icon = "BatteryChargingFull", Position = 4, Description = "Sets the AC and the DC fast charge limit, each from 0.5 to 1.0 in steps of 0.1.")]
    public Task SetChargeLimitsAsync(
        [OperationParameter(Unit = StateUnit.Percent)] decimal chargeLimit,
        [OperationParameter(Unit = StateUnit.Percent)] decimal fastChargeLimit,
        CancellationToken cancellationToken)
    {
        var acPercent = BluelinkValues.ToChargeLimitPercent(chargeLimit, nameof(chargeLimit));
        var dcPercent = BluelinkValues.ToChargeLimitPercent(fastChargeLimit, nameof(fastChargeLimit));
        return _vehicle.Commands.SetChargeLimitsAsync(_vehicle, acPercent, dcPercent, cancellationToken);
    }

    internal void Apply(BluelinkVehicleStatus status)
    {
        BatteryLevel = status.BatteryLevel;
        IsPluggedIn = status.IsPluggedIn;
        IsCharging = status.IsCharging;
        ChargingPower = status.ChargingPower;
        ChargeLimit = status.ChargeLimit;
        FastChargeLimit = status.FastChargeLimit;
        RemainingChargingTime = status.RemainingChargingTime;
        Range = status.Range;
    }
}
