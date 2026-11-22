namespace Namotion.Devices.Hyundai;

/// <summary>The account side of the operations of a vehicle and its children.</summary>
internal interface IBluelinkVehicleCommands
{
    /// <summary>
    /// Whether commands can be sent now: authenticated, enough requests left for the command to finish, and a PIN for a
    /// CCS2 vehicle. A command that needs no PIN passes <see cref="BluelinkProtocol.Legacy"/>.
    /// </summary>
    bool CanSendCommands(BluelinkProtocol protocol);

    /// <summary>Whether a forced refresh can be sent now: authenticated, enough requests left for it to finish, and the 12 V level at least the minimum.</summary>
    bool CanRefresh(decimal? auxiliaryBatteryLevel);

    Task RefreshAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken);

    Task SetChargingAsync(BluelinkVehicle vehicle, bool start, CancellationToken cancellationToken);

    Task SetChargeLimitsAsync(BluelinkVehicle vehicle, int acPercent, int dcPercent, CancellationToken cancellationToken);

    Task StartClimateAsync(BluelinkVehicle vehicle, decimal temperature, bool defrost, CancellationToken cancellationToken);

    Task StopClimateAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken);
}
