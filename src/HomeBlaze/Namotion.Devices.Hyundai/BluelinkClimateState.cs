using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Vehicles;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Hyundai;

/// <summary>The climate of a Bluelink vehicle.</summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class BluelinkClimateState : IVehicleClimateState, IVehicleClimateController, ITitleProvider, IIconProvider
{
    private readonly BluelinkVehicle _vehicle;

    internal BluelinkClimateState(BluelinkVehicle vehicle)
    {
        _vehicle = vehicle;
        IsClimateOn = null;
        TemperatureSetting = null;
        IsDefrostOn = null;
        IsSteeringWheelHeaterOn = null;
        OutsideTemperature = null;
    }

    public string Title => "Climate";

    public string IconName => "Thermostat";

    [Category("")]
    [State(IsDiscrete = true, Position = 1)] public partial bool? IsClimateOn { get; internal set; }

    /// <summary>The set temperature, or null while the climate is off.</summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)] public partial decimal? TemperatureSetting { get; internal set; }

    [State(IsDiscrete = true, Position = 3)] public partial bool? IsDefrostOn { get; internal set; }
    [State(IsDiscrete = true, Position = 4)] public partial bool? IsSteeringWheelHeaterOn { get; internal set; }

    /// <summary>Not reported by the Bluelink API; always null.</summary>
    [Derived]
    [Category("")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 5)]
    public decimal? InsideTemperature => null;

    [Category("")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 6)] public partial decimal? OutsideTemperature { get; internal set; }

    [Derived]
    [PropertyAttribute("StartClimate", KnownAttributes.IsEnabled)]
    public bool StartClimate_IsEnabled => !_vehicle.IsBusy && _vehicle.Commands.CanSendCommands(_vehicle.Protocol);

    [Derived]
    [PropertyAttribute("StartClimateWithDefrost", KnownAttributes.IsEnabled)]
    public bool StartClimateWithDefrost_IsEnabled => !_vehicle.IsBusy && _vehicle.Commands.CanSendCommands(_vehicle.Protocol);

    [Derived]
    [PropertyAttribute("StopClimate", KnownAttributes.IsEnabled)]
    public bool StopClimate_IsEnabled => !_vehicle.IsBusy && _vehicle.Commands.CanSendCommands(_vehicle.Protocol);

    /// <inheritdoc />
    public Task StartClimateAsync(decimal temperature, CancellationToken cancellationToken) =>
        StartAsync(temperature, defrost: false, cancellationToken);

    /// <summary>Starts the climate with the front windshield defrost.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The temperature is not 14.0 to 31.5 °C in steps of 0.5.</exception>
    [Operation(Title = "Start Climate With Defrost", Icon = "AcUnit", Position = 3, Description = "Starts the climate with the front windshield defrost, for the duration set in the account.")]
    [Category("Climate")]
    public Task StartClimateWithDefrostAsync([OperationParameter(Unit = StateUnit.DegreeCelsius)] decimal temperature, CancellationToken cancellationToken) =>
        StartAsync(temperature, defrost: true, cancellationToken);

    /// <inheritdoc />
    public Task StopClimateAsync(CancellationToken cancellationToken) =>
        _vehicle.Commands.StopClimateAsync(_vehicle, cancellationToken);

    internal void Apply(BluelinkVehicleStatus status)
    {
        IsClimateOn = status.IsClimateOn;
        TemperatureSetting = status.TemperatureSetting;
        IsDefrostOn = status.IsDefrostOn;
        IsSteeringWheelHeaterOn = status.IsSteeringWheelHeaterOn;
        OutsideTemperature = status.OutsideTemperature;
    }

    private Task StartAsync(decimal temperature, bool defrost, CancellationToken cancellationToken)
    {
        BluelinkValues.ValidateTemperature(temperature);
        return _vehicle.Commands.StartClimateAsync(_vehicle, temperature, defrost, cancellationToken);
    }
}
