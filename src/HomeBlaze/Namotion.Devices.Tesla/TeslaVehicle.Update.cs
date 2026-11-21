using CarServer;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;

namespace Namotion.Devices.Tesla;

public partial class TeslaVehicle
{
    private static readonly Func<VehicleData, Timestamp?>[] SnapshotTimestamps =
    [
        snapshot => snapshot.ChargeState?.Timestamp,
        snapshot => snapshot.ClimateState?.Timestamp,
        snapshot => snapshot.DriveState?.Timestamp,
        snapshot => snapshot.LocationState?.Timestamp,
        snapshot => snapshot.ClosuresState?.Timestamp,
        snapshot => snapshot.TirePressureState?.Timestamp,
        snapshot => snapshot.MediaState?.Timestamp,
        snapshot => snapshot.MediaDetailState?.Timestamp,
        snapshot => snapshot.SoftwareUpdateState?.Timestamp
    ];

    /// <summary>
    /// Applies an entry of /api/1/products: state, names and the cached snapshot. Never wakes the vehicle.
    /// </summary>
    internal void ApplyProduct(TeslaProductDto product)
    {
        VehicleState = TeslaValues.Enum<TeslaVehicleState>(product.State, _logger) ?? TeslaVehicleState.Unknown;
        DisplayName = TeslaValues.Text(product.DisplayName);
        CommandSigning = TeslaValues.Enum<TeslaCommandSigning>(product.CommandSigning, _logger);
        IsInService = product.InService;

        if (TeslaValues.Text(product.CachedData) is { } cachedData)
        {
            ApplyCachedData(cachedData);
        }
    }

    /// <summary>
    /// Applies a vehicle_data response of an awake vehicle.
    /// </summary>
    internal void ApplyVehicleData(TeslaVehicleDataDto data)
    {
        VehicleState = TeslaValues.Enum<TeslaVehicleState>(data.State, _logger) ?? VehicleState;

        if (data.ChargeState is { } charge) Charge.Update(charge);
        if (data.ClimateState is { } climate) Climate.Update(climate);
        if (data.DriveState is { } drive) Drive.Update(drive);
        if (data.GuiSettings is { } guiSettings) DisplaySettings.Update(guiSettings);
        if (data.VehicleConfig is { } config) Specification.Update(config);
        if (data.VehicleState is { } state) ApplyVehicleState(state);

        LastUpdated = _timeProvider.GetUtcNow();
    }

    private void ApplyVehicleState(TeslaVehicleStateDto state)
    {
        var timestamp = TeslaValues.FromUnixMilliseconds(state.Timestamp);

        Body.Update(state);
        Media.Update(state);
        if (state.SoftwareUpdate is { } update) SoftwareUpdate.Update(update, timestamp);
        if (state.SpeedLimitMode is { } speedLimit) SpeedLimit.Update(speedLimit, timestamp);

        Drive.IsHomelinkNearby = state.HomelinkNearby;
        VehicleName = TeslaValues.Text(state.VehicleName);
        SoftwareVersion = TeslaValues.Text(state.CarVersion);

        // The snapshot reports hundredths of a mile, so the JSON miles are rounded to match.
        if (_odometerClock.TryAdvance(timestamp))
        {
            TotalDrivenDistance = TeslaValues.MilesToKilometers(state.Odometer is { } miles ? Math.Round(miles, 2, MidpointRounding.AwayFromZero) : null);
        }
    }

    private void ApplyCachedData(string cachedData)
    {
        VehicleData snapshot;
        try
        {
            snapshot = VehicleData.Parser.ParseFrom(Convert.FromBase64String(cachedData));
        }
        catch (Exception exception) when (exception is FormatException or InvalidProtocolBufferException)
        {
            _logger.LogWarning(exception, "The cached data of Tesla vehicle {Vin} could not be read.", Vin);
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(VehicleData snapshot)
    {
        // A section that the snapshot does not carry is null.
        if (snapshot.ChargeState is { } charge) Charge.Update(charge);
        if (snapshot.ClimateState is { } climate) Climate.Update(climate);
        if (snapshot.DriveState is { } drive) ApplyDriveSnapshot(drive);
        if (snapshot.LocationState is { } location) Drive.Update(location);
        if (snapshot.ClosuresState is { } closures) ApplyClosuresSnapshot(closures);
        if (snapshot.TirePressureState is { } tires) Body.Update(tires);
        if (snapshot.MediaState is { } media) Media.Update(media);
        if (snapshot.MediaDetailState is { } mediaDetail) Media.Update(mediaDetail);
        if (snapshot.SoftwareUpdateState is { } update) SoftwareUpdate.Update(update);

        UpdateLastUpdatedFromSnapshot(snapshot);
    }

    private void ApplyDriveSnapshot(DriveState drive)
    {
        Drive.Update(drive);

        if (drive.OptionalOdometerInHundredthsOfAMileCase != DriveState.OptionalOdometerInHundredthsOfAMileOneofCase.None &&
            _odometerClock.TryAdvance(drive.Timestamp?.ToDateTimeOffset()))
        {
            TotalDrivenDistance = TeslaValues.MilesToKilometers(drive.OdometerInHundredthsOfAMile / 100m);
        }
    }

    private void ApplyClosuresSnapshot(ClosuresState closures)
    {
        Body.Update(closures);

        if (closures.SpeedLimitMode is { } speedLimit)
        {
            SpeedLimit.Update(speedLimit, closures.Timestamp?.ToDateTimeOffset());
        }
    }

    private void UpdateLastUpdatedFromSnapshot(VehicleData snapshot)
    {
        DateTimeOffset? newest = null;
        foreach (var getTimestamp in SnapshotTimestamps)
        {
            if (getTimestamp(snapshot)?.ToDateTimeOffset() is { } timestamp && (newest is null || timestamp > newest))
            {
                newest = timestamp;
            }
        }

        if (newest is { } value && (LastUpdated is null || value > LastUpdated))
        {
            LastUpdated = value;
        }
    }
}
