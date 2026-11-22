using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Devices.Vehicles;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Hyundai;

/// <summary>An electric vehicle of a Bluelink account, discovered from the account and keyed by VIN.</summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class BluelinkVehicle : IElectricVehicle, IDeviceInfo, ILastUpdatedProvider, ITitleProvider, IIconProvider
{
    private DateTimeOffset? _appliedStatusTime;

    // 1 while a refresh or command of this vehicle runs; only the caller that set it writes IsBusy.
    private int _operationRunning;

    internal BluelinkVehicle(IBluelinkVehicleCommands commands, BluelinkVehicleInfo info)
    {
        Commands = commands;
        VehicleId = info.VehicleId;

        Vin = info.Vin;
        Nickname = null;
        ModelName = null;
        Protocol = info.Protocol;
        AuxiliaryBatteryLevel = null;
        LastUpdated = null;
        TotalDrivenDistance = null;
        Location = null;

        Charge = new BluelinkChargeState(this);
        Climate = new BluelinkClimateState(this);
        Body = new BluelinkVehicleBody();

        UpdateInfo(info);
    }

    /// <summary>The account, held in a plain property so that it is not attached under its own vehicle.</summary>
    internal IBluelinkVehicleCommands Commands { get; }

    /// <summary>The ID of the vehicle in the vehicle API.</summary>
    internal string VehicleId { get; private set; }

    /// <summary>The vehicle identification number.</summary>
    [State(Position = 1)] public partial string Vin { get; internal set; }

    /// <summary>The name of the vehicle in the Bluelink app.</summary>
    [State(Position = 2)] public partial string? Nickname { get; internal set; }

    /// <summary>The model name from the vehicle list, such as KONA.</summary>
    [State(Position = 3)] public partial string? ModelName { get; internal set; }

    [State(IsDiscrete = true, Position = 4)] public partial BluelinkProtocol Protocol { get; internal set; }

    /// <summary>The charge of the 12 V battery; null when the sensor is unreliable.</summary>
    [State(Unit = StateUnit.Percent, Position = 5)] public partial decimal? AuxiliaryBatteryLevel { get; internal set; }

    /// <summary>The time of the stored status; the read time when the status has none.</summary>
    [State(Position = 6)] public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>True while a Refresh or command of this vehicle runs.</summary>
    [State(IsDiscrete = true, Position = 7)] public partial bool IsBusy { get; internal set; }

    [State(Position = 400)] public partial BluelinkChargeState Charge { get; internal set; }
    [State(Position = 401)] public partial BluelinkClimateState Climate { get; internal set; }
    [State(Position = 402)] public partial BluelinkVehicleBody Body { get; internal set; }

    // IElectricVehicle

    public partial decimal? TotalDrivenDistance { get; internal set; }

    /// <summary>The stored parking location.</summary>
    public partial GeoCoordinate? Location { get; internal set; }

    IVehicleChargingState IElectricVehicle.Charge => Charge;

    IVehicleClimateState? IVehicle.Climate => Climate;

    IDoorLockState? IVehicle.Body => Body;

    // IDeviceInfo

    [Derived] public string? Manufacturer => "Hyundai";

    [Derived] public string? Model => ModelName;

    [Derived] public string? ProductCode => null;

    [Derived] public string? SerialNumber => Vin;

    [Derived] public string? HardwareRevision => null;

    // Display

    [Derived] public string? Title => Nickname ?? $"Hyundai: {BluelinkValues.VinSuffix(Vin)}";

    [Derived] public string IconName => "DirectionsCar";

    [Derived] public string? IconColor => Charge.IsCharging == true ? "Success" : null;

    // Operations

    [Derived]
    [PropertyAttribute("Refresh", KnownAttributes.IsEnabled)]
    public bool Refresh_IsEnabled => !IsBusy && Commands.CanRefresh(AuxiliaryBatteryLevel);

    /// <summary>Asks the vehicle to upload its status, then reads it. Reaches the vehicle and costs 12 V battery.</summary>
    [Operation(Title = "Refresh", Icon = "Refresh", RequiresConfirmation = true,
        Description = "Asks the vehicle to upload its status. Reaches the vehicle and costs 12 V battery, so use it rarely. Refused while the 12 V battery is low.")]
    public Task RefreshAsync(CancellationToken cancellationToken) => Commands.RefreshAsync(this, cancellationToken);

    /// <summary>Marks the vehicle busy unless another operation of it runs; a true result must be paired with <see cref="EndOperation"/>.</summary>
    internal bool TryStartOperation()
    {
        if (Interlocked.CompareExchange(ref _operationRunning, 1, 0) != 0)
        {
            return false;
        }

        IsBusy = true;
        return true;
    }

    /// <summary>Ends the operation started with a successful <see cref="TryStartOperation"/>.</summary>
    internal void EndOperation()
    {
        // Cleared before the slot is freed, so that it cannot overwrite the true of the next operation.
        IsBusy = false;
        Volatile.Write(ref _operationRunning, 0);
    }

    internal void UpdateInfo(BluelinkVehicleInfo info)
    {
        VehicleId = info.VehicleId;
        Nickname = info.Nickname;
        ModelName = info.ModelName;
        Protocol = info.Protocol;
    }

    /// <summary>Applies a stored status; a status older than the newest applied status time is skipped. A status without time never blocks a later one.</summary>
    internal void ApplyStatus(BluelinkVehicleStatus status, DateTimeOffset readTime)
    {
        if (status.Time is { } time)
        {
            if (time < _appliedStatusTime)
            {
                return;
            }

            _appliedStatusTime = time;
        }

        LastUpdated = status.Time ?? readTime;
        TotalDrivenDistance = status.TotalDrivenDistance;
        AuxiliaryBatteryLevel = status.AuxiliaryBatteryLevel;
        Charge.Apply(status);
        Climate.Apply(status);
        Body.Apply(status);
    }

    /// <summary>Applies the stored parking location; a missing location keeps the previous one.</summary>
    internal void ApplyLocation(GeoCoordinate? location)
    {
        if (location is not null)
        {
            Location = location;
        }
    }
}
