using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Devices.Vehicles;
using HomeBlaze.Abstractions.Networking;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// A Tesla vehicle of a Tesla account, discovered from the account.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaVehicle :
    IElectricVehicle,
    IDeviceInfo,
    ISoftwareState,
    IConnectionState,
    ILastUpdatedProvider,
    ITitleProvider,
    IIconProvider
{
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    // The JSON odometer is in the vehicle state and the snapshot odometer in the drive state, each with its own timestamp.
    private readonly TeslaSectionClock _odometerClock = new();

    private Func<TeslaVehicle, CancellationToken, Task>? _wakeUp;
    private bool _hasWarnedAboutCachedData;

    // The last cached data that was parsed; a sleeping vehicle returns the same snapshot with every poll.
    private string? _appliedCachedData;

    internal TeslaVehicle(long id, string vin, TimeProvider timeProvider, ILogger logger)
    {
        Id = id;
        _timeProvider = timeProvider;
        _logger = logger;

        Vin = vin;
        DisplayName = null;
        VehicleName = null;
        VehicleState = TeslaVehicleState.Unknown;
        CommandSigning = null;
        IsInService = null;
        LastUpdated = null;
        TotalDrivenDistance = null;
        Location = null;
        SoftwareVersion = null;

        Charge = new TeslaChargeState(logger);
        Climate = new TeslaClimateState(logger);
        Drive = new TeslaDriveState(this);
        Body = new TeslaVehicleBody();
        Media = new TeslaMedia(logger);
        SoftwareUpdate = new TeslaSoftwareUpdate(logger);
        SpeedLimit = new TeslaSpeedLimitMode();
        Specification = new TeslaVehicleSpecification();
        DisplaySettings = new TeslaDisplaySettings();
    }

    /// <summary>The ID of the vehicle in the Owner API.</summary>
    internal long Id { get; }

    // Identity and status

    /// <summary>The vehicle identification number.</summary>
    [State(Position = 1)] public partial string Vin { get; internal set; }

    /// <summary>The name of the vehicle in the Tesla account.</summary>
    [State(Position = 2)] public partial string? DisplayName { get; internal set; }

    /// <summary>The name that is set in the vehicle.</summary>
    [State(Position = 3)] public partial string? VehicleName { get; internal set; }

    [State(IsDiscrete = true, Position = 4)] public partial TeslaVehicleState VehicleState { get; internal set; }

    /// <summary>Whether the vehicle requires signed commands.</summary>
    [State(IsDiscrete = true, Position = 5)] public partial TeslaCommandSigning? CommandSigning { get; internal set; }

    /// <summary>Whether the vehicle is in service at Tesla.</summary>
    [State(IsDiscrete = true, Position = 6)] public partial bool? IsInService { get; internal set; }

    /// <summary>Whether the vehicle is awake, or null when its state is unknown.</summary>
    [Derived]
    [State(IsDiscrete = true, Position = 7)]
    public bool? IsAwake => VehicleState switch
    {
        TeslaVehicleState.Online => true,
        TeslaVehicleState.Asleep or TeslaVehicleState.Offline => false,
        _ => null
    };

    /// <summary>Whether the vehicle is reachable through Tesla's servers, which an asleep vehicle is.</summary>
    [Derived]
    public bool IsConnected => VehicleState is TeslaVehicleState.Online or TeslaVehicleState.Asleep;

    /// <summary>The time of the newest data: the read time of the vehicle data, or the time of the cached snapshot.</summary>
    [State(Position = 8)] public partial DateTimeOffset? LastUpdated { get; internal set; }

    // Children

    [State(Position = 400)] public partial TeslaChargeState Charge { get; internal set; }
    [State(Position = 401)] public partial TeslaClimateState Climate { get; internal set; }
    [State(Position = 402)] public partial TeslaDriveState Drive { get; internal set; }
    [State(Position = 403)] public partial TeslaVehicleBody Body { get; internal set; }
    [State(Position = 404)] public partial TeslaMedia Media { get; internal set; }
    [State(Position = 405)] public partial TeslaSoftwareUpdate SoftwareUpdate { get; internal set; }
    [State(Position = 406)] public partial TeslaSpeedLimitMode SpeedLimit { get; internal set; }
    [State(Position = 407)] public partial TeslaVehicleSpecification Specification { get; internal set; }
    [State(Position = 408)] public partial TeslaDisplaySettings DisplaySettings { get; internal set; }

    // IElectricVehicle

    public partial decimal? TotalDrivenDistance { get; internal set; }

    /// <summary>The location, which the drive state sets from the location section.</summary>
    public partial GeoCoordinate? Location { get; internal set; }

    IVehicleChargingState IElectricVehicle.Charge => Charge;

    IVehicleClimateState? IVehicle.Climate => Climate;

    IDoorLockState? IVehicle.Body => Body;

    // IDeviceInfo, ISoftwareState

    [Derived] public string? Manufacturer => "Tesla";

    [Derived]
    public string? Model => Specification.CarType switch
    {
        null => null,
        "model3" => "Model 3",
        "modely" => "Model Y",
        "models" or "models2" => "Model S",
        "modelx" => "Model X",
        "cybertruck" => "Cybertruck",
        var other => other
    };

    [Derived] public string? ProductCode => Specification.TrimBadging;

    [Derived] public string? SerialNumber => Vin;

    [Derived] public string? HardwareRevision => null;

    public partial string? SoftwareVersion { get; internal set; }

    [Derived] public string? AvailableSoftwareUpdate => SoftwareUpdate.Version;

    // Display

    [Derived]
    public string? Title => VehicleName ?? DisplayName ?? $"{Model ?? "Tesla"}: {Vin[^Math.Min(6, Vin.Length)..]}";

    [Derived] public string IconName => "DirectionsCar";

    [Derived]
    public string? IconColor => (Charge.IsCharging, IsAwake) switch
    {
        (true, _) => "Success",
        (_, true) => "Info",
        _ => null
    };

    // Activity, read by the polling of the account

    /// <summary>Whether the vehicle charges, drives or has a user in it.</summary>
    internal bool IsActive => Charge.IsCharging == true || Drive.IsDriving == true || Body.IsUserPresent == true;

    /// <summary>Whether nothing keeps the vehicle awake, so that it may fall asleep when it is not read.</summary>
    internal bool CanSleep => !IsActive && Climate.IsClimateOn != true && Body.IsSentryModeOn != true;

    // Operations

    // Reads a field, whose change is not tracked: the account sets the handler before it attaches the vehicle.
    [Derived]
    [PropertyAttribute("WakeUp", KnownAttributes.IsEnabled)]
    public bool WakeUp_IsEnabled => VehicleState is TeslaVehicleState.Asleep or TeslaVehicleState.Offline && _wakeUp is not null;

    /// <summary>
    /// Wakes the vehicle, waits until it is online and reads its data. This is the only call that wakes the vehicle.
    /// </summary>
    [Operation(Title = "Wake Up", Icon = "Alarm", Description = "Wakes the vehicle so that its data is read. Waking costs battery; the vehicle falls asleep again by itself.")]
    public Task WakeUpAsync(CancellationToken cancellationToken) =>
        _wakeUp?.Invoke(this, cancellationToken) ?? Task.CompletedTask;

    /// <summary>Sets the handler of <see cref="WakeUpAsync"/>; call it before the vehicle is attached.</summary>
    internal void SetWakeUpHandler(Func<TeslaVehicle, CancellationToken, Task> wakeUp) => _wakeUp = wakeUp;
}
